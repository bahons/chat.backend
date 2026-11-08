using chat.Api.Businesses;
using chat.Api.Endpoints;
using chat.Api.Middleware;
using chat.Application.Messaging;
using chat.Contracts;
using chat.Contracts.Messaging;
using chat.Domain.Businesses;
using chat.Domain.Identity;
using chat.Domain.Messaging;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace chat.Api.Messaging;

internal sealed class ConversationService(ChatDbContext db, ConversationAccess access, BusinessService businesses,
    TimeProvider clock)
{
    public async Task<ConversationView> CreateAsync(Guid actor, Guid businessId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var business = await businesses.LockBusinessAsync(businessId, ct);
        if (business.Status != BusinessStatus.VERIFIED || !business.IsActive)
        {
            throw new ApiFailure(StatusCodes.Status404NotFound);
        }

        var conversation = await db.Conversations.SingleOrDefaultAsync(row => row.ClientUserId == actor
            && row.BusinessId == businessId, ct);
        if (conversation is null)
        {
            var now = PostgresTimestamp();
            conversation = new Conversation { ClientUserId = actor, BusinessId = businessId, CreatedAt = now, UpdatedAt = now };
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync(ct);
        }

        var view = await DetailAsync(conversation.Id, actor, UserKind.CLIENT, ct);
        await transaction.CommitAsync(ct);
        return view;
    }

    public async Task<PageResponse<ConversationView>> ListAsync(Guid actor, UserKind kind, CursorPage page, CancellationToken ct)
    {
        var query = access.VisibleTo(actor, kind);
        if (page.Before is { } before)
        {
            query = page.StableInbox
                ? query.Where(row => EF.Functions.LessThan(ValueTuple.Create(row.CreatedAt, row.Id), ValueTuple.Create(before, page.BeforeId)))
                : query.Where(row => EF.Functions.LessThan(ValueTuple.Create(row.UpdatedAt, row.Id), ValueTuple.Create(before, page.BeforeId)));
        }

        var views = ViewRows(query);
        var ordered = page.StableInbox ? views.OrderByDescending(row => row.Conversation.CreatedAt)
            : views.OrderByDescending(row => row.Conversation.UpdatedAt);
        var rows = await ordered.ThenByDescending(row => row.Conversation.Id).Take(page.Limit + 1).ToListAsync(ct);
        var side = ConversationAccess.SideOf(kind);
        return page.MakePage(rows, row => View(row, side),
            row => page.StableInbox ? row.Conversation.CreatedAt : row.Conversation.UpdatedAt, row => row.Conversation.Id);
    }

    public async Task<ConversationView> DetailAsync(Guid id, Guid actor, UserKind kind, CancellationToken ct)
    {
        var row = await ViewRows(access.VisibleTo(actor, kind).Where(conversation => conversation.Id == id))
            .SingleOrDefaultAsync(ct) ?? throw new ApiFailure(StatusCodes.Status404NotFound);
        return View(row, ConversationAccess.SideOf(kind));
    }

    private IQueryable<ConversationRow> ViewRows(IQueryable<Conversation> visible)
    {
        var receipts = db.MessageReadReceipts.AsNoTracking();
        return from conversation in visible
               join business in db.Businesses.AsNoTracking() on conversation.BusinessId equals business.Id
               join client in receipts.Where(row => row.Side == ParticipantSide.CLIENT)
                   on conversation.Id equals client.ConversationId into clientReceipts
               from client in clientReceipts.DefaultIfEmpty()
               join staff in receipts.Where(row => row.Side == ParticipantSide.BUSINESS)
                   on conversation.Id equals staff.ConversationId into staffReceipts
               from staff in staffReceipts.DefaultIfEmpty()
               select new ConversationRow
               {
                   Conversation = conversation,
                   DisplayName = business.DisplayName,
                   Client = client,
                   Business = staff
               };
    }

    public async Task<PageResponse<MessageView>> ListMessagesAsync(Guid id, Guid actor, UserKind kind,
        CursorPage page, CancellationToken ct)
    {
        await access.RequireAsync(id, actor, kind, ct);
        var query = db.Messages.AsNoTracking().Where(message => message.ConversationId == id
            && access.VisibleTo(actor, kind).Any(conversation => conversation.Id == message.ConversationId));
        if (page.Before is { } before)
        {
            query = query.Where(row => EF.Functions.LessThan(ValueTuple.Create(row.CreatedAt, row.Id),
                ValueTuple.Create(before, page.BeforeId)));
        }

        var rows = await query.OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id)
            .Take(page.Limit + 1).ToListAsync(ct);
        var messageIds = rows.Take(page.Limit).Select(row => row.Id).ToArray();
        var attachments = await db.Attachments.AsNoTracking().Where(row => row.MessageId != null
                && messageIds.Contains(row.MessageId.Value))
            .OrderBy(row => row.Id).ToListAsync(ct);
        var byMessage = attachments.ToLookup(row => row.MessageId);
        return page.MakePage(rows, row => MessagePresentation.View(row, byMessage[row.Id]), row => row.CreatedAt, row => row.Id);
    }

    public async Task<MessageView> SendAsync(Guid id, Guid actor, UserKind kind, SendMessageRequest request, CancellationToken ct)
    {
        var attachmentIds = request.AttachmentIds ?? [];
        var type = Enum.Parse<MessageType>(request.Type);
        if (request.ClientMessageId == Guid.Empty || attachmentIds.Distinct().Count() != attachmentIds.Length
            || (type == MessageType.TEXT ? string.IsNullOrWhiteSpace(request.Text) || attachmentIds.Length != 0
                : attachmentIds.Length == 0))
        {
            throw new ApiFailure(StatusCodes.Status400BadRequest);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var conversation = await access.LockAsync(id, actor, kind, ct);
        var existing = await db.Messages.AsNoTracking().SingleOrDefaultAsync(message => message.ConversationId == id
            && message.AuthorUserId == actor && message.ClientMessageId == request.ClientMessageId, ct);
        if (existing is not null)
        {
            var attached = await db.Attachments.AsNoTracking().Where(row => row.MessageId == existing.Id)
                .OrderBy(row => row.Id).ToListAsync(ct);
            return MessagePresentation.View(existing, attached);
        }

        var uploads = await db.Attachments.Where(row => attachmentIds.Contains(row.Id)
                && row.ConversationId == id && row.UploaderUserId == actor && row.MessageId == null
                && row.Status == AttachmentStatus.READY && row.RetiredAt == null)
            .OrderBy(row => row.Id).ToListAsync(ct);
        if (uploads.Count != attachmentIds.Length || (type == MessageType.IMAGE
                && uploads.Any(row => !row.ContentType.StartsWith("image/", StringComparison.Ordinal))))
        {
            throw new ApiFailure(StatusCodes.Status409Conflict, "MEDIA_NOT_READY", "An attachment is unavailable.");
        }

        await Outbox.RequireCapacityAsync(db, 2, ct);
        var now = PostgresTimestamp();
        var message = new Message
        {
            ConversationId = id,
            AuthorUserId = actor,
            AuthorSide = ConversationAccess.SideOf(kind),
            ClientMessageId = request.ClientMessageId,
            Type = type,
            Text = request.Text,
            CreatedAt = now
        };
        db.Messages.Add(message);
        foreach (var upload in uploads)
        {
            upload.MessageId = message.Id;
        }
        conversation.UpdatedAt = now;
        conversation.LastMessageId = message.Id;
        conversation.LastMessagePreview = MessagePresentation.Preview(request.Text);
        var result = MessagePresentation.View(message, uploads);
        Outbox.Add(db, Outbox.MessageCreated, now, new { messageId = message.Id }, id);
        Outbox.Add(db, Outbox.ConversationUpdated, now, new { messageId = message.Id }, id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<ReadReceiptView> ReadAsync(Guid id, Guid actor, UserKind kind, Guid messageId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await access.LockAsync(id, actor, kind, ct);
        var message = await db.Messages.AsNoTracking().SingleOrDefaultAsync(row => row.Id == messageId
            && row.ConversationId == id, ct) ?? throw new ApiFailure(StatusCodes.Status404NotFound);
        var side = ConversationAccess.SideOf(kind);
        var current = await db.MessageReadReceipts.FindAsync([id, side], ct);
        if (current is not null)
        {
            var candidateIsNewer = await db.Messages.AnyAsync(row => row.Id == messageId &&
                EF.Functions.GreaterThan(ValueTuple.Create(row.CreatedAt, row.Id),
                    ValueTuple.Create(current.LastMessageAt, current.LastMessageId)), ct);
            if (!candidateIsNewer)
            {
                return new ReadReceiptView(id, side.ToString(), current.LastMessageId, current.UpdatedAt);
            }
        }

        await Outbox.RequireCapacityAsync(db, 1, ct);
        var now = PostgresTimestamp();
        if (current is not null && now <= current.UpdatedAt)
        {
            now = current.UpdatedAt.AddTicks(10);
        }

        if (current is null)
        {
            current = new MessageReadReceipt
            {
                ConversationId = id,
                Side = side,
                LastMessageId = messageId,
                LastMessageAt = message.CreatedAt,
                UpdatedAt = now
            };
            db.MessageReadReceipts.Add(current);
        }
        else
        {
            current.LastMessageId = messageId;
            current.LastMessageAt = message.CreatedAt;
            current.UpdatedAt = now;
        }

        Outbox.Add(db, Outbox.ConversationReadUpdated, now,
            new { conversationId = id, readerSide = side.ToString(), lastMessageId = messageId, updatedAt = now }, id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new ReadReceiptView(id, side.ToString(), messageId, now);
    }

    private DateTimeOffset PostgresTimestamp()
    {
        var now = clock.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    private sealed class ConversationRow
    {
        public required Conversation Conversation { get; init; }
        public required string DisplayName { get; init; }
        public MessageReadReceipt? Client { get; init; }
        public MessageReadReceipt? Business { get; init; }
    }

    private static ConversationView View(ConversationRow row, ParticipantSide side)
    {
        var conversation = row.Conversation;
        return new ConversationView(conversation.Id, conversation.BusinessId, row.DisplayName, conversation.CreatedAt,
            conversation.UpdatedAt, conversation.LastMessageId, conversation.LastMessagePreview, side.ToString(),
            new ConversationReadState(Position(row.Client), Position(row.Business)));
    }

    private static ReadPosition? Position(MessageReadReceipt? receipt) =>
        receipt is null ? null : new ReadPosition(receipt.LastMessageId, receipt.UpdatedAt);

}
