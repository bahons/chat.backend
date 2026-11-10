using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace chat.Api.Messaging;

internal sealed partial class WorkerBacklogMonitor(IServiceScopeFactory scopes, IOptions<PushOptions> push,
    TimeProvider clock, ILogger<WorkerBacklogMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckBacklogAsync(stoppingToken);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                MonitorFailed(logger);
            }
        }
    }

    internal async Task CheckBacklogAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var now = clock.GetUtcNow();
        await CheckAsync("outbox", db.OutboxMessages.Where(row => row.CompletedAt == null
            && row.OccurredAt < now.AddMinutes(-2)), ct);
        await CheckAsync("mail", db.AccountMails.Where(row => row.CompletedAt == null
            && row.CreatedAt < now.AddMinutes(-2)), ct);
        if (push.Value.Enabled)
        {
            await CheckAsync("push", db.PushDeliveries.Where(row => row.CompletedAt == null
                && row.AvailableAt < now.AddMinutes(-2)), ct);
        }
        var retention = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<RetentionOptions>>().CurrentValue;
        if (retention.AllowsCleanup(scope.ServiceProvider.GetRequiredService<IHostEnvironment>()))
        {
            await CheckAsync("media-cleanup", db.Attachments.Where(row => row.ObjectsDeletedAt == null
                && row.UploadExpiresAt < now.AddHours(-retention.UnattachedMediaHours - 1)), ct);
        }
    }

    private async Task CheckAsync<T>(string queue, IQueryable<T> overdue, CancellationToken ct)
    {
        var count = await overdue.Take(1001).CountAsync(ct);
        WorkerMetrics.Overdue.Record(count, new KeyValuePair<string, object?>("queue", queue));
        if (count > 0)
        {
            Backlog(logger, queue, count);
        }
    }

    [LoggerMessage(EventId = 233, Level = LogLevel.Warning, Message = "Worker backlog overdue: queue={Queue} count_capped={Count}")]
    private static partial void Backlog(ILogger logger, string queue, int count);

    [LoggerMessage(EventId = 234, Level = LogLevel.Warning, Message = "Worker backlog probe unavailable")]
    private static partial void MonitorFailed(ILogger logger);
}
