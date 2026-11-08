using chat.Api.Messaging;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace chat.Api.Businesses;

internal sealed partial class ExpiredDraftWorker(IServiceScopeFactory scopeFactory, TimeProvider clock,
    IOptionsMonitor<RetentionOptions> options, IHostEnvironment environment,
    ILogger<ExpiredDraftWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                if (!options.CurrentValue.AllowsCleanup(environment))
                {
                    continue;
                }

                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
                var now = clock.GetUtcNow();
                var expired = db.ApplicationDrafts.Where(draft => draft.ConsumedAt == null && draft.ExpiresAt <= now
                    && !db.VerificationDocuments.Any(document => document.DraftId == draft.Id && document.ApplicationId != null));
                await expired.Where(draft => expired.OrderBy(candidate => candidate.ExpiresAt).ThenBy(candidate => candidate.Id)
                        .Take(200).Select(candidate => candidate.Id).Contains(draft.Id))
                    .ExecuteDeleteAsync(stoppingToken);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                CleanupFailed(logger);
            }
        }
    }

    [LoggerMessage(EventId = 221, Level = LogLevel.Warning, Message = "Expired verification draft cleanup failed")]
    private static partial void CleanupFailed(ILogger logger);
}
