using System.Security.Cryptography;
using System.Text;
using chat.Api.DependencyInjection;
using chat.Api.Middleware;
using chat.Domain.Identity;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace chat.Api.Identity;

internal sealed class AccountMailQueue(ChatDbContext db, AccountMailCipher cipher, IOptions<AccountEmailOptions> options,
    TimeProvider clock)
{
    public async Task CheckCapacityAsync(CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Mail admission requires an account transaction.");
        }

        var admitted = await db.Database.SqlQuery<bool>(
            $"""SELECT pg_try_advisory_xact_lock(924601, 2) AS "Value" """).SingleAsync(ct);
        if (!admitted || !cipher.IsConfigured || await db.AccountMails.CountAsync(row => row.CompletedAt == null, ct) >= options.Value.QueueCapacity)
        {
            throw new ApiFailure(StatusCodes.Status503ServiceUnavailable);
        }
    }

    public void Enqueue(Guid userId, bool reset, string token, DateTimeOffset expiresAt)
    {
        var now = clock.GetUtcNow();
        var row = new AccountMail { UserId = userId, IsPasswordReset = reset, CreatedAt = now, AvailableAt = now, ExpiresAt = expiresAt };
        row.Payload = cipher.Encrypt(row, token);
        db.AccountMails.Add(row);
    }
}

internal sealed class AccountMailCipher(IOptions<AccountEmailOptions> options, IOptions<JwtSettings> jwt, IHostEnvironment environment)
{
    public bool IsConfigured => ParseKey(options.Value.QueueKeyBase64) is not null
        || (options.Value.QueueKeyBase64 is null && !environment.IsProduction() && jwt.Value.GetSigningKey() is not null);

    public static byte[]? ParseKey(string? value)
    {
        Span<byte> bytes = stackalloc byte[32];
        return Convert.TryFromBase64String(value ?? "", bytes, out var count) && count == bytes.Length ? bytes.ToArray() : null;
    }

    public byte[] Encrypt(AccountMail row, string token)
    {
        var plaintext = Encoding.ASCII.GetBytes(token);
        var payload = new byte[12 + 16 + plaintext.Length];
        try
        {
            RandomNumberGenerator.Fill(payload.AsSpan(0, 12));
            using var cipher = CreateCipher();
            cipher.Encrypt(payload.AsSpan(0, 12), plaintext, payload.AsSpan(28), payload.AsSpan(12, 16), AssociatedData(row));
            return payload;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public string Decrypt(AccountMail row)
    {
        var payload = row.Payload;
        if (payload is not { Length: 71 })
        {
            throw new CryptographicException("Invalid account mail payload.");
        }

        Span<byte> plaintext = stackalloc byte[43];
        try
        {
            using var cipher = CreateCipher();
            cipher.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(28), payload.AsSpan(12, 16), plaintext, AssociatedData(row));
            return Encoding.ASCII.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private AesGcm CreateCipher()
    {
        var key = ParseKey(options.Value.QueueKeyBase64);
        if (key is null && options.Value.QueueKeyBase64 is null && !environment.IsProduction()
            && jwt.Value.GetSigningKey() is { } developmentKey)
        {
            key = HMACSHA256.HashData(developmentKey.Key, "chat.account-mail.v1"u8);
        }

        if (key is null) throw new CryptographicException("Account mail key unavailable.");
        try
        {
            return new AesGcm(key, 16);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] AssociatedData(AccountMail row) =>
        [.. row.Id.ToByteArray(), .. row.UserId.ToByteArray(), row.IsPasswordReset ? (byte)1 : (byte)0];
}
