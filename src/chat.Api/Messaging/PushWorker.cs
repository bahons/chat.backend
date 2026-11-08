using chat.Api.Identity;
using chat.Application.Push;
using chat.Domain.Messaging;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace chat.Api.Messaging;

internal sealed partial class PushWorker(IServiceScopeFactory scopes, IOptions<PushOptions> options, TimeProvider clock,
    ILogger<PushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                for (var i = 0; i < 25 && await DeliverNextAsync(stoppingToken); i++)
                {
                }
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                PushFailed(logger);
            }
        }
    }

    private async Task<bool> DeliverNextAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var now = clock.GetUtcNow();
        var delivery = await WorkerLease.ClaimAsync<PushDelivery>(db, $"""
            SELECT * FROM "PushDeliveries"
            WHERE "CompletedAt" IS NULL AND "AvailableAt" <= {now}
            ORDER BY "AvailableAt", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
            """, now, ct);
        if (delivery is null)
        {
            return false;
        }

        var device = await db.DeviceRegistrations.AsNoTracking().SingleOrDefaultAsync(row => row.Id == delivery.DeviceId
            && row.UserId == delivery.UserId && row.FamilyId == delivery.FamilyId && row.RevokedAt == null
            && row.UpdatedAt > now - DeviceRegistry.InactivityLimit, ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(row => row.Id == delivery.UserId && row.IsActive
            && row.EmailVerifiedAt != null && AuthPrincipal.ActiveSessions(db, now)
                .Any(session => session.UserId == row.Id && session.FamilyId == delivery.FamilyId), ct);
        var access = scope.ServiceProvider.GetRequiredService<ConversationAccess>();
        if (delivery.ExpiresAt <= now || delivery.Attempts > 16)
        {
            WorkerLease.Finish(delivery, now, "dead");
            PushFailed(logger);
        }
        else if (device is null || user is null
                 || !await access.VisibleTo(user.Id, user.Kind).AnyAsync(row => row.Id == delivery.ConversationId, ct))
        {
            WorkerLease.Finish(delivery, now, "suppressed");
        }
        else
        {
            var gateway = scope.ServiceProvider.GetRequiredService<IPushGateway>();
            PushResult result;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                result = delivery.TicketId is { } ticket
                    ? await gateway.CheckAsync(ticket, timeout.Token)
                    : await gateway.SendAsync(device.PushToken, delivery.EventId, delivery.ConversationId, timeout.Token);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                result = new PushResult(PushResultKind.Retry);
            }

            now = clock.GetUtcNow();
            if (result.Kind == PushResultKind.Unregistered)
            {
                // Lease fencing and the device generation check both participate in the same short transaction.
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                WorkerLease.Finish(delivery, now, "unregistered");
                await db.SaveChangesAsync(ct);
                await db.DeviceRegistrations.Where(row => row.Id == device.Id && row.UserId == device.UserId
                        && row.FamilyId == device.FamilyId && row.UpdatedAt == device.UpdatedAt && row.RevokedAt == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.RevokedAt, now), ct);
                await transaction.CommitAsync(ct);
                return true;
            }

            switch (result.Kind)
            {
                case PushResultKind.Accepted:
                    delivery.TicketId = result.TicketId;
                    delivery.AvailableAt = now.AddMinutes(15);
                    delivery.LeaseId = null;
                    break;
                case PushResultKind.Retry:
                    WorkerLease.Retry(delivery, now, 900);
                    PushFailed(logger);
                    break;
                default:
                    WorkerLease.Finish(delivery, now, result.Kind == PushResultKind.Delivered ? "submitted" : "dead");
                    if (result.Kind == PushResultKind.Rejected) PushFailed(logger);
                    break;
            }
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    [LoggerMessage(EventId = 226, Level = LogLevel.Warning, Message = "Push delivery failed or exhausted")]
    private static partial void PushFailed(ILogger logger);
}
