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
    public int SweepIntervalSeconds { get; set; } = 30;
    public int MaxBatchesPerSweep { get; set; } = 50;
    public int SweepBudgetSeconds { get; set; } = 5;

    public bool AllowsCleanup(IHostEnvironment environment) => !LegalHold && (Approved || environment.IsDevelopment());
}

internal sealed partial class RetentionWorker(IServiceScopeFactory scopes, IOptionsMonitor<RetentionOptions> options,
    TimeProvider clock, ILogger<RetentionWorker> logger) : BackgroundService
{
    private const int Batch = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                Failed(logger);
            }
            await Task.Delay(TimeSpan.FromSeconds(options.CurrentValue.SweepIntervalSeconds), clock, stoppingToken);
        }
    }

    internal async Task DrainAsync(CancellationToken ct)
    {
        var settings = options.CurrentValue;
        var started = clock.GetTimestamp();
        try
        {
            for (var batch = 0; batch < settings.MaxBatchesPerSweep
                && clock.GetElapsedTime(started).TotalSeconds < settings.SweepBudgetSeconds; batch++)
            {
                ct.ThrowIfCancellationRequested();
                // SweepAsync reads the current approval and hold before every bounded batch.
                if (!await SweepAsync(ct)) break;
            }
        }
        finally
        {
            WorkerMetrics.RetentionDuration.Record(clock.GetElapsedTime(started).TotalSeconds);
        }
    }

    internal async Task<bool> SweepAsync(CancellationToken ct)
    {
        var policy = options.CurrentValue;
        if (!policy.Approved || policy.LegalHold)
        {
            return false;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var now = clock.GetUtcNow();
        var sessionsBefore = now.AddDays(-policy.ExpiredSessionsDays);
        var workBefore = now.AddDays(-policy.CompletedWorkDays);
        var sessions = await db.RefreshSessions.Where(row => row.ExpiresAt < sessionsBefore)
            .OrderBy(row => row.ExpiresAt).ThenBy(row => row.Id).Take(Batch).ExecuteDeleteAsync(ct);
        WorkerMetrics.RecordRetention("sessions", sessions);
        var outbox = await db.OutboxMessages.Where(row => row.CompletedAt < workBefore)
            .OrderBy(row => row.CompletedAt).ThenBy(row => row.Id).Take(Batch).ExecuteDeleteAsync(ct);
        WorkerMetrics.RecordRetention("outbox", outbox);
        var push = await db.PushDeliveries.Where(row => row.CompletedAt < workBefore)
            .OrderBy(row => row.CompletedAt).ThenBy(row => row.Id).Take(Batch).ExecuteDeleteAsync(ct);
        WorkerMetrics.RecordRetention("push", push);
        var mail = await db.AccountMails.Where(row => row.CompletedAt < workBefore)
            .OrderBy(row => row.CompletedAt).ThenBy(row => row.Id).Take(Batch).ExecuteDeleteAsync(ct);
        WorkerMetrics.RecordRetention("mail", mail);
        var staleDevices = db.DeviceRegistrations.Where(row => row.RevokedAt == null
            && (row.UpdatedAt < now - DeviceRegistry.InactivityLimit
                || !db.RefreshSessions.Any(session => session.UserId == row.UserId && session.FamilyId == row.FamilyId
                    && session.ExpiresAt > now && session.ConsumedAt == null && session.RevokedAt == null)));
        var devices = await staleDevices.Where(row => staleDevices.OrderBy(candidate => candidate.UpdatedAt)
                .ThenBy(candidate => candidate.Id).Take(Batch).Select(candidate => candidate.Id).Contains(row.Id))
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.RevokedAt, now), ct);
        WorkerMetrics.RecordRetention("devices", devices);
        if (sessions + outbox + push + mail + devices > 0)
        {
            Cleaned(logger, sessions, outbox, push, mail, devices);
        }
        return sessions == Batch || outbox == Batch || push == Batch || mail == Batch || devices == Batch;
    }

    [LoggerMessage(EventId = 236, Level = LogLevel.Warning, Message = "Retention sweep failed; remaining records will retry")]
    private static partial void Failed(ILogger logger);

    [LoggerMessage(EventId = 237, Level = LogLevel.Information,
        Message = "Retention sweep sessions={Sessions} outbox={Outbox} push={Push} mail={Mail} devicesRevoked={Devices}")]
    private static partial void Cleaned(ILogger logger, int sessions, int outbox, int push, int mail, int devices);
}
