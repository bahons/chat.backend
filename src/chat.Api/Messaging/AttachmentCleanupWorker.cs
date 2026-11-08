using chat.Application.Media;
using chat.Domain.Messaging;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace chat.Api.Messaging;

internal sealed partial class AttachmentCleanupWorker(IServiceScopeFactory scopes, TimeProvider clock,
    IOptionsMonitor<RetentionOptions> options, IHostEnvironment environment,
    ILogger<AttachmentCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                for (var i = 0; i < 20 && await CleanNextAsync(stoppingToken); i++)
                {
                }
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                CleanupFailed(logger);
            }
        }
    }

    internal async Task<bool> CleanNextAsync(CancellationToken ct)
    {
        var policy = options.CurrentValue;
        if (!policy.AllowsCleanup(environment))
        {
            return false;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var now = clock.GetUtcNow();
        var candidate = await db.Attachments.AsNoTracking()
            .Where(row => row.ObjectsDeletedAt == null && row.UploadExpiresAt <= now.AddHours(-policy.UnattachedMediaHours)
                && (row.CleanupAfter == null || row.CleanupAfter <= now))
            .OrderBy(row => row.UploadExpiresAt).ThenBy(row => row.Id).FirstOrDefaultAsync(ct);
        if (candidate is null)
        {
            return false;
        }

        var storage = scope.ServiceProvider.GetRequiredService<IPrivateObjectStorage>();
        Attachment upload;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            // Serialize the retirement decision with send and complete on the same conversation.
            await db.Conversations.FromSql($"SELECT * FROM \"Conversations\" WHERE \"Id\" = {candidate.ConversationId} FOR UPDATE")
                .AsNoTracking().ToListAsync(ct);
            upload = await db.Attachments.SingleAsync(row => row.Id == candidate.Id, ct);
            if (upload.ObjectsDeletedAt is not null || upload.CleanupAfter > now)
            {
                return true;
            }

            if (upload.MessageId is null)
            {
                upload.RetiredAt ??= now;
            }

            upload.CleanupAttempts = Math.Min(20, upload.CleanupAttempts + 1);
            upload.CleanupAfter = now.AddSeconds(Math.Max(30, Math.Min(3600, Math.Pow(2, upload.CleanupAttempts))));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await storage.DeleteAsync(MediaService.StagingKey(upload.Id), timeout.Token);
            if (upload.RetiredAt is not null)
            {
                await storage.DeleteAsync(MediaService.VerifiedKey(upload.Id), timeout.Token);
            }

            upload.ObjectsDeletedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            CleanupCompleted(logger, upload.RetiredAt is not null);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            CleanupFailed(logger);
        }

        return true;
    }

    [LoggerMessage(EventId = 231, Level = LogLevel.Warning, Message = "Attachment cleanup failed; scheduled for retry")]
    private static partial void CleanupFailed(ILogger logger);

    [LoggerMessage(EventId = 232, Level = LogLevel.Information, Message = "Attachment cleanup completed; retired={Retired}")]
    private static partial void CleanupCompleted(ILogger logger, bool retired);
}
