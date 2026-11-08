namespace chat.Api.Messaging;

internal sealed partial class RealtimeRevocationWorker(IServiceScopeFactory scopes, RealtimeConnections connections,
    ILogger<RealtimeRevocationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var snapshot = connections.Snapshot;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<RealtimeAccess>().RecheckAsync(snapshot, timeout.Token)
                    .WaitAsync(timeout.Token);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                foreach (var connection in snapshot)
                {
                    connection.Abort();
                }
                RecheckFailed(logger);
            }
        }
    }

    [LoggerMessage(EventId = 235, Level = LogLevel.Warning, Message = "Realtime ACL sweep failed; connections closed")]
    private static partial void RecheckFailed(ILogger logger);
}
