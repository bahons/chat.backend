using chat.Api.Middleware;
using chat.Domain.Businesses;
using chat.Domain.Identity;
using chat.Domain.Messaging;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace chat.Api.Messaging;

internal sealed class ConversationAccess(ChatDbContext db)
{
    public IQueryable<Conversation> VisibleTo(Guid actor, UserKind kind)
    {
        var available = db.Conversations.AsNoTracking().Where(conversation => db.Businesses.Any(business =>
            business.Id == conversation.BusinessId && business.Status == BusinessStatus.VERIFIED && business.IsActive));
        return kind switch
        {
            UserKind.CLIENT => available.Where(conversation => conversation.ClientUserId == actor),
            UserKind.BUSINESS_MEMBER => available.Where(conversation => EligibleMemberships(db).Any(membership =>
                membership.BusinessId == conversation.BusinessId && membership.UserId == actor)),
            _ => available.Where(_ => false)
        };
    }

    public async Task<Conversation> RequireAsync(Guid id, Guid actor, UserKind kind, CancellationToken ct) =>
        await VisibleTo(actor, kind).SingleOrDefaultAsync(conversation => conversation.Id == id, ct)
        ?? throw new ApiFailure(StatusCodes.Status404NotFound);

    public async Task<Conversation> LockAsync(Guid id, Guid actor, UserKind kind, CancellationToken ct)
    {
        var allowed = await RequireAsync(id, actor, kind, ct);
        if (!await LockActiveBusinessAsync(allowed.BusinessId, ct)
            || (kind == UserKind.BUSINESS_MEMBER && !await LockMembershipAsync(allowed.BusinessId, actor, ct)))
        {
            throw new ApiFailure(StatusCodes.Status404NotFound);
        }

        var rows = await db.Conversations.FromSql(
            $"SELECT * FROM \"Conversations\" WHERE \"Id\" = {id} FOR UPDATE").ToListAsync(ct);
        return rows.SingleOrDefault() ?? throw new ApiFailure(StatusCodes.Status404NotFound);
    }

    public static IQueryable<BusinessMembership> EligibleMemberships(ChatDbContext db) =>
        db.BusinessMemberships.Where(membership => membership.IsActive && (membership.Role == BusinessRole.OWNER
            || membership.Role == BusinessRole.ADMIN || membership.Role == BusinessRole.AGENT));

    public async Task<bool> LockActiveBusinessAsync(Guid businessId, CancellationToken ct)
    {
        var rows = await db.Businesses.FromSql(
            $"SELECT * FROM \"Businesses\" WHERE \"Id\" = {businessId} FOR SHARE").ToListAsync(ct);
        return rows.SingleOrDefault() is { IsActive: true, Status: BusinessStatus.VERIFIED };
    }

    public async Task<bool> LockMembershipAsync(Guid businessId, Guid userId, CancellationToken ct)
    {
        var rows = await db.BusinessMemberships.FromSql(
            $"SELECT * FROM \"BusinessMemberships\" WHERE \"UserId\" = {userId} AND \"BusinessId\" = {businessId} FOR SHARE")
            .ToListAsync(ct);
        return rows.SingleOrDefault() is { IsActive: true, Role: BusinessRole.OWNER or BusinessRole.ADMIN or BusinessRole.AGENT };
    }

    public static ParticipantSide SideOf(UserKind kind) =>
        kind == UserKind.CLIENT ? ParticipantSide.CLIENT : ParticipantSide.BUSINESS;
}
