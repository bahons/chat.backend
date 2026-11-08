using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace chat.Api.Messaging;

internal sealed class RetentionOptions
{
    public bool Approved { get; set; }
    public bool LegalHold { get; set; }
    public int ExpiredSessionsDays { get; set; } = 7;
    public int CompletedWorkDays { get; set; } = 7;
    public int UnattachedMediaHours { get; set; } = 24;

    public bool AllowsCleanup(IHostEnvironment environment) => !LegalHold && (Approved || environment.IsDevelopment());
}

internal sealed partial class RetentionWorker(IServiceScopeFactory scopes, IOptionsMonitor<RetentionOptions> options,
    TimeProvider clock, ILogger<RetentionWorker> logger) : BackgroundService
{
    private const int Batch = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                Failed(logger);
            }
        }
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        var policy = options.CurrentValue;
        if (!policy.Approved || policy.LegalHold)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var now = clock.GetUtcNow();
        var sessionsBefore = now.AddDays(-policy.ExpiredSessionsDays);
        var workBefore = now.AddDays(-policy.CompletedWorkDays);
        var sessions = await db.RefreshSessions.Where(row => row.ExpiresAt < sessionsBefore)
            .OrderBy(row => row.ExpiresAt).ThenBy(row => row.Id).Take(Batch).ExecuteDeleteAsync(ct);
        var outbox = await db.OutboxMessages.Where(row => row.CompletedAt < workBefore)
            .OrderBy(row => row.CompletedAt).ThenBy(row => row.Id).Take(Batch).ExecuteDeleteAsync(ct);
        var push = await db.PushDeliveries.Where(row => row.CompletedAt < workBefore)
            .OrderBy(row => row.CompletedAt).ThenBy(row => row.Id).Take(Batch).ExecuteDeleteAsync(ct);
        var mail = await db.AccountMails.Where(row => row.CompletedAt < workBefore)
            .OrderBy(row => row.CompletedAt).ThenBy(row => row.Id).Take(Batch).ExecuteDeleteAsync(ct);
        var staleDevices = db.DeviceRegistrations.Where(row => row.RevokedAt == null
            && (row.UpdatedAt < now - DeviceRegistry.InactivityLimit
                || !db.RefreshSessions.Any(session => session.UserId == row.UserId && session.FamilyId == row.FamilyId
                    && session.ExpiresAt > now && session.ConsumedAt == null && session.RevokedAt == null)));
        var devices = await staleDevices.Where(row => staleDevices.OrderBy(candidate => candidate.UpdatedAt)
                .ThenBy(candidate => candidate.Id).Take(Batch).Select(candidate => candidate.Id).Contains(row.Id))
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.RevokedAt, now), ct);
        if (sessions + outbox + push + mail + devices > 0)
        {
            Cleaned(logger, sessions, outbox, push, mail, devices);
        }
    }

    [LoggerMessage(EventId = 236, Level = LogLevel.Warning, Message = "Retention sweep failed; remaining records will retry")]
    private static partial void Failed(ILogger logger);

    [LoggerMessage(EventId = 237, Level = LogLevel.Information,
        Message = "Retention sweep sessions={Sessions} outbox={Outbox} push={Push} mail={Mail} devicesRevoked={Devices}")]
    private static partial void Cleaned(ILogger logger, int sessions, int outbox, int push, int mail, int devices);
}
