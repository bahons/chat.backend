using Microsoft.Extensions.Options;
using Npgsql;

namespace chat.Api.DependencyInjection;

internal sealed partial class SingleReplicaGuard(IOptions<DatabaseOptions> options, IHostApplicationLifetime lifetime,
    ILogger<SingleReplicaGuard> logger) : BackgroundService
{
    private NpgsqlConnection? session;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = new NpgsqlConnectionStringBuilder(options.Value.Postgres)
        {
            Pooling = false,
            Multiplexing = false,
            Timeout = 5,
            CommandTimeout = 3,
            KeepAlive = 5
        };
        var connection = new NpgsqlConnection(settings.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var acquire = new NpgsqlCommand("SELECT pg_try_advisory_lock(924601, 1)", connection);
            if (await acquire.ExecuteScalarAsync(cancellationToken) is not true)
            {
                throw new InvalidOperationException("Another chat API instance owns the database dispatch lock.");
            }

            session = connection;
            await base.StartAsync(cancellationToken);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await using var probe = new NpgsqlCommand("""
                    SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND pid = pg_backend_pid()
                        AND classid = 924601 AND objid = 1 AND objsubid = 2 AND mode = 'ExclusiveLock' AND granted)
                    """, session!);
                if (await probe.ExecuteScalarAsync(stoppingToken) is not true)
                {
                    throw new InvalidOperationException("Dispatch lock lost.");
                }
            }
        }
        catch (Exception) when (!stoppingToken.IsCancellationRequested)
        {
            LockLost(logger);
            lifetime.StopApplication();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken);
        }
        finally
        {
            if (session is not null) await session.DisposeAsync();
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        session?.Dispose();
    }

    [LoggerMessage(EventId = 228, Level = LogLevel.Critical, Message = "Single replica dispatch lock lost; shutting down")]
    private static partial void LockLost(ILogger logger);
}
