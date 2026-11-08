using System.Buffers.Text;
using chat.Api.Messaging;
using chat.Application.Media;
using chat.Domain.Businesses;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace chat.Api.Businesses;

internal sealed partial class VerificationDocumentScanner(IServiceScopeFactory scopes,
    IOptions<FileScanOptions> configuration, TimeProvider clock, ILogger<VerificationDocumentScanner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.Value.Host))
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                for (var i = 0; i < 8 && await ScanNextAsync(stoppingToken); i++)
                {
                }
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                ScanFailed(logger);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<bool> ScanNextAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var pending = await db.VerificationDocuments.FromSql($"""
            SELECT * FROM "VerificationDocuments"
            WHERE "ScanStatus" = 'PENDING' AND ("ScanAfter" IS NULL OR "ScanAfter" <= {clock.GetUtcNow()})
            ORDER BY "ScanAfter" NULLS FIRST, "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);
        if (pending.SingleOrDefault() is not { } document)
        {
            return false;
        }

        var scanner = scope.ServiceProvider.GetRequiredService<IFileScanner>();
        var matchesFormat = document.Size == document.Content.Length
            && BusinessDrafts.DetectContentType(document.Content) == document.ContentType
            && (document.ContentType != "application/pdf" || HasPdfEnvelope(document.Content));

        try
        {
            document.ScanStatus = matchesFormat && await scanner.IsCleanAsync(document.Content, cancellationToken)
                ? VerificationScanStatus.READY
                : VerificationScanStatus.QUARANTINED;
            document.ScanAfter = null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
            exception is IOException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            document.ScanAfter = clock.GetUtcNow().AddSeconds(30);
            ScanFailed(logger);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    internal static bool HasPdfEnvelope(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16 || !(bytes.StartsWith("%PDF-1."u8) || bytes.StartsWith("%PDF-2.0"u8)))
        {
            return false;
        }

        var end = TrimPdfWhitespace(bytes);
        if (!end.EndsWith("%%EOF"u8))
        {
            return false;
        }

        var preceding = TrimPdfWhitespace(end[..^5]);
        var marker = preceding.LastIndexOf("startxref"u8);
        if (marker < 0)
        {
            return false;
        }

        var offset = TrimPdfWhitespace(preceding[(marker + "startxref"u8.Length)..]);
        while (!offset.IsEmpty && IsPdfWhitespace(offset[0]))
        {
            offset = offset[1..];
        }

        return Utf8Parser.TryParse(offset, out int xref, out var consumed)
            && consumed == offset.Length && xref > 0 && xref < marker;
    }

    private static ReadOnlySpan<byte> TrimPdfWhitespace(ReadOnlySpan<byte> value)
    {
        while (!value.IsEmpty && IsPdfWhitespace(value[^1]))
        {
            value = value[..^1];
        }

        return value;
    }

    private static bool IsPdfWhitespace(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;

    [LoggerMessage(EventId = 227, Level = LogLevel.Warning, Message = "Verification document scan unavailable, retrying")]
    private static partial void ScanFailed(ILogger logger);
}
