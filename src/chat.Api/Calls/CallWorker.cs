using chat.Api.Identity;
using chat.Api.Messaging;
using chat.Domain.Calls;
using chat.Domain.Identity;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace chat.Api.Calls;

internal sealed partial class CallWorker(IServiceScopeFactory scopes, ILogger<CallWorker> logger) : BackgroundService
{
    private Guid? after;
    private readonly SemaphoreSlim sweep = new(1, 1);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { await SweepAsync(timeout.Token); }
            catch (Exception) when (!stoppingToken.IsCancellationRequested) { SweepFailed(logger); }
        }
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        await sweep.WaitAsync(ct);
        try { await SweepBatchAsync(ct); }
        finally { sweep.Release(); }
    }

    private async Task SweepBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var query = db.Calls.AsNoTracking().Where(row => row.State == CallState.RINGING
            || row.State == CallState.CONNECTING || row.State == CallState.ACTIVE);
        if (after is { } cursor) query = query.Where(row => row.Id.CompareTo(cursor) > 0);
        var ids = await query.OrderBy(row => row.Id).Select(row => row.Id).Take(256).ToArrayAsync(ct);
        after = ids.Length == 256 ? ids[^1] : null;
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var access = scope.ServiceProvider.GetRequiredService<ConversationAccess>();
        var identity = scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value;
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var snapshot = await db.Calls.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
            if (snapshot is null) continue;
            // Use the same conversation -> call lock order as request handlers.
            await db.Conversations.FromSql($"SELECT * FROM \"Conversations\" WHERE \"Id\" = {snapshot.ConversationId} FOR UPDATE")
                .LoadAsync(ct);
            var call = (await db.Calls.FromSql($"SELECT * FROM \"Calls\" WHERE \"Id\" = {id} FOR UPDATE").ToArrayAsync(ct)).Single();
            if (!call.IsLive) continue;
            async Task<bool> Eligible(Guid userId, Guid familyId, UserKind kind)
            {
                return await db.Users.AnyAsync(user => user.Id == userId && user.IsActive && user.Kind == kind
                        && (!identity.RequireConfirmedEmail || user.EmailVerifiedAt != null), ct)
                    && await AuthPrincipal.ActiveSessions(db, clock.GetUtcNow())
                        .AnyAsync(session => session.UserId == userId && session.FamilyId == familyId, ct)
                    && await access.VisibleTo(userId, kind).AnyAsync(room => room.Id == call.ConversationId, ct);
            }
            var valid = await Eligible(call.InitiatorUserId, call.InitiatorFamilyId,
                call.InitiatorIsClient ? UserKind.CLIENT : UserKind.BUSINESS_MEMBER);
            if (valid && call.State != CallState.RINGING)
                valid = await Eligible(call.ClientUserId, call.ClientFamilyId!.Value, UserKind.CLIENT)
                    && await Eligible(call.OperatorUserId!.Value, call.OperatorFamilyId!.Value, UserKind.BUSINESS_MEMBER);
            var changed = valid ? call.Expire(clock.GetUtcNow()) : call.Finish(clock.GetUtcNow(), CallState.ENDED, "ACCESS_REVOKED");
            if (changed)
            {
                scope.ServiceProvider.GetRequiredService<CallService>().AddState(call);
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
    }

    [LoggerMessage(EventId = 260, Level = LogLevel.Warning, Message = "Call TTL or access sweep failed")]
    private static partial void SweepFailed(ILogger logger);
}

