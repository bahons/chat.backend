using System.Text.Json;
using chat.Api.Hubs;
using chat.Application.Messaging;
using chat.Contracts.Messaging;
using chat.Domain.Messaging;
using chat.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace chat.Api.Messaging;

internal sealed partial class OutboxWorker(IServiceScopeFactory scopes, RealtimeConnections connections,
    IHubContext<MessagingHub> hub, IOptions<PushOptions> push, TimeProvider clock, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await DispatchNextAsync(stoppingToken))
                {
                    continue;
                }
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                DispatchFailed(logger);
            }

            // Poll when idle or unavailable; an existing backlog must not wait for another tick.
            await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
        }
    }

    private async Task<bool> DispatchNextAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var now = clock.GetUtcNow();
        var row = await WorkerLease.ClaimAsync<OutboxMessage>(db, $"""
            SELECT * FROM "OutboxMessages"
            WHERE "CompletedAt" IS NULL AND "AvailableAt" <= {now}
            ORDER BY "AvailableAt", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
            """, now, ct);
        if (row is null)
        {
            return false;
        }

        if (row.Attempts > 8 || row.OccurredAt <= now.AddHours(-1))
        {
            WorkerLease.Finish(row, now, "dead");
            DispatchFailed(logger);
        }
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var outcome = await DispatchAsync(db, scope.ServiceProvider.GetRequiredService<RealtimeAccess>(), row, timeout.Token);
                WorkerLease.Finish(row, clock.GetUtcNow(), outcome);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                foreach (var entry in db.ChangeTracker.Entries<PushDelivery>().Where(entry => entry.State == EntityState.Added))
                {
                    entry.State = EntityState.Detached;
                }

                WorkerLease.Retry(row, clock.GetUtcNow());
                DispatchFailed(logger);
            }
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<string> DispatchAsync(ChatDbContext db, RealtimeAccess access, OutboxMessage row, CancellationToken ct)
    {
        // Revocation is enforced by the independent ACL sweep. No suspension payload goes to revoked peers.
        if (row.EventName == Outbox.BusinessAccessRevoked || row.ConversationId is not { } id)
        {
            return "suppressed";
        }

        var payload = JsonSerializer.Deserialize<JsonElement>(row.Payload, Outbox.Json);
        var message = payload.TryGetProperty("messageId", out var reference) && reference.ValueKind == JsonValueKind.String
            && reference.TryGetGuid(out var messageId)
            ? await db.Messages.AsNoTracking().SingleOrDefaultAsync(item => item.Id == messageId && item.ConversationId == id, ct)
            : null;
        if (row.EventName is Outbox.MessageCreated or Outbox.ConversationUpdated)
        {
            if (message is null)
            {
                return "suppressed";
            }

            if (row.EventName == Outbox.MessageCreated)
            {
                var attachments = await db.Attachments.AsNoTracking().Where(item => item.MessageId == message.Id
                    && item.ConversationId == id).OrderBy(item => item.Id).ToArrayAsync(ct);
                payload = JsonSerializer.SerializeToElement(new { message = MessagePresentation.View(message, attachments) }, Outbox.Json);
            }
            else
            {
                payload = JsonSerializer.SerializeToElement(new
                {
                    conversationId = id,
                    lastMessagePreview = MessagePresentation.Preview(message.Text),
                    updatedAt = message.CreatedAt
                }, Outbox.Json);
            }
        }

        var envelope = new EventEnvelope<JsonElement>(row.Id, 1, row.OccurredAt, id, payload);
        if (row.EventName == Outbox.MessageCreated && push.Value.Enabled)
        {
            await QueuePushAsync(db, row.Id, id, message!.AuthorSide == ParticipantSide.CLIENT, ct);
        }

        var recipients = await access.RecipientsAsync(id, connections.Snapshot, ct);
        if (recipients.Length == 0)
        {
            return "offline";
        }

        await hub.Clients.Clients(recipients).SendAsync(row.EventName, envelope, ct);
        return "submitted";
    }

    private async Task QueuePushAsync(ChatDbContext db, Guid eventId, Guid id, bool sentByClient, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var conversation = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        if (conversation is null)
        {
            return;
        }

        var users = sentByClient
            ? ConversationAccess.EligibleMemberships(db).Where(membership => membership.BusinessId == conversation.BusinessId)
                .Select(membership => membership.UserId)
            : db.Users.Where(user => user.Id == conversation.ClientUserId).Select(user => user.Id);
        const int pushCapacity = 10000;
        var queued = await db.PushDeliveries.Where(row => row.CompletedAt == null).Take(pushCapacity).CountAsync(ct);
        var available = Math.Min(256, pushCapacity - queued);
        var devices = await db.DeviceRegistrations.AsNoTracking().Where(device => device.RevokedAt == null
                && device.UpdatedAt > now - DeviceRegistry.InactivityLimit && users.Contains(device.UserId)
                && !db.PushDeliveries.Any(row => row.EventId == eventId && row.DeviceId == device.Id))
            .OrderByDescending(device => device.UpdatedAt).ThenBy(device => device.Id).Take(available + 1).ToArrayAsync(ct);
        if (devices.Length > available)
        {
            FanoutLimited(logger);
        }

        db.PushDeliveries.AddRange(devices.Take(available).Select(device => new PushDelivery
        {
            EventId = eventId,
            DeviceId = device.Id,
            UserId = device.UserId,
            FamilyId = device.FamilyId,
            ConversationId = conversation.Id,
            AvailableAt = now,
            ExpiresAt = now.AddHours(23)
        }));
    }

    [LoggerMessage(EventId = 225, Level = LogLevel.Warning, Message = "Realtime outbox dispatch failed or exhausted")]
    private static partial void DispatchFailed(ILogger logger);

    [LoggerMessage(EventId = 229, Level = LogLevel.Warning, Message = "Push fanout limit reached; remaining devices use REST recovery")]
    private static partial void FanoutLimited(ILogger logger);
}
