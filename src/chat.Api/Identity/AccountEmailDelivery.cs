using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using chat.Api.Messaging;
using chat.Application.Identity;
using chat.Domain.Identity;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace chat.Api.Identity;

internal sealed class SmtpAccountEmailSender(IOptions<AccountEmailOptions> options) : IAccountEmailSender
{
    public async Task SendAsync(AccountEmail message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var client = new SmtpClient(settings.Host, settings.Port) { EnableSsl = settings.EnableTls };
        if (settings.Username is not null)
        {
            client.Credentials = new NetworkCredential(settings.Username, settings.Password);
        }

        using var mail = new MailMessage(settings.From, message.Recipient, message.Subject, message.Body);
        if (message.IntentId is { } id)
        {
            mail.Headers.Add("Message-ID", $"<{id:N}@{mail.From!.Host}>");
        }

        await client.SendMailAsync(mail, cancellationToken);
    }
}

internal sealed partial class AccountEmailDelivery(IServiceScopeFactory scopes, AccountMailCipher cipher, TimeProvider clock,
    IAccountEmailSender sender, ILogger<AccountEmailDelivery> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!cipher.IsConfigured)
            {
                continue;
            }

            try
            {
                for (var i = 0; i < 16 && await DeliverNextAsync(stoppingToken); i++)
                {
                }
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                DeliveryFailed(logger);
            }
        }
    }

    private async Task<bool> DeliverNextAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var now = clock.GetUtcNow();
        var item = await WorkerLease.ClaimAsync<AccountMail>(db, $"""
            SELECT * FROM "AccountMails"
            WHERE "CompletedAt" IS NULL AND "AvailableAt" <= {now}
            ORDER BY "AvailableAt", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
            """, now, ct);
        if (item is null)
        {
            return false;
        }

        try
        {
            if (item.ExpiresAt <= now)
            {
                WorkerLease.Finish(item, now, "expired");
            }
            else if (item.Attempts > 8)
            {
                WorkerLease.Finish(item, now, "dead");
                DeliveryFailed(logger);
            }
            else
            {
                await SendAsync(db, item, now, ct);
            }
        }
        catch (CryptographicException)
        {
            WorkerLease.Finish(item, now, "dead");
            DeliveryFailed(logger);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            WorkerLease.Retry(item, clock.GetUtcNow());
            DeliveryFailed(logger);
        }

        if (item.CompletedAt is not null)
        {
            item.Payload = null;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task SendAsync(ChatDbContext db, AccountMail item, DateTimeOffset now, CancellationToken ct)
    {
        var token = cipher.Decrypt(item);
        var hash = AccountCredentials.TokenHash(token);
        var email = await db.Users.Where(user => user.Id == item.UserId && user.IsActive
                && (item.IsPasswordReset
                    ? user.EmailVerifiedAt != null && user.ResetHash == hash && user.ResetExpiresAt > now
                    : user.EmailVerifiedAt == null && user.VerificationHash == hash && user.VerificationExpiresAt > now))
            .Select(user => user.Email).SingleOrDefaultAsync(ct);
        if (email is null)
        {
            WorkerLease.Finish(item, now, "superseded");
            return;
        }

        var message = item.IsPasswordReset
            ? new AccountEmail(email, "chat password reset", $"Password reset code: {token}", item.Id)
            : new AccountEmail(email, "chat email verification", $"Verification code: {token}", item.Id);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await sender.SendAsync(message, timeout.Token);
        WorkerLease.Finish(item, clock.GetUtcNow(), "submitted");
    }

    [LoggerMessage(EventId = 210, Level = LogLevel.Warning, Message = "Account email delivery failed or exhausted")]
    private static partial void DeliveryFailed(ILogger logger);
}
