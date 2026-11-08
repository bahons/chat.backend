using chat.Api.Identity;
using chat.Api.Middleware;
using chat.Contracts.Businesses;
using chat.Domain.Businesses;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace chat.Api.Businesses;

internal sealed class BusinessDrafts(ChatDbContext db, TimeProvider clock)
{
    public const string TokenHeader = "X-Application-Draft";
    private const int MaxDocumentSize = 5 * 1024 * 1024;

    public async Task<BusinessDraftResponse> CreateAsync(CancellationToken ct)
    {
        var token = AccountCredentials.NewToken();
        var draft = new ApplicationDraft
        {
            TokenHash = AccountCredentials.TokenHash(token),
            ExpiresAt = clock.GetUtcNow().AddMinutes(30)
        };
        db.ApplicationDrafts.Add(draft);
        await db.SaveChangesAsync(ct);
        return new BusinessDraftResponse(draft.Id, token, draft.ExpiresAt);
    }

    public async Task<VerificationDocumentInfo> UploadAsync(Guid id, string token, HttpRequest request, CancellationToken ct)
    {
        if (token.Length != 43)
        {
            throw new ApiFailure(StatusCodes.Status404NotFound);
        }

        var hash = AccountCredentials.TokenHash(token);
        var now = clock.GetUtcNow();
        if (!await db.ApplicationDrafts.AnyAsync(draft => draft.Id == id && draft.TokenHash == hash
            && draft.ConsumedAt == null && draft.ExpiresAt > now, ct))
        {
            throw new ApiFailure(StatusCodes.Status404NotFound);
        }

        if (request.ContentLength > MaxDocumentSize)
        {
            throw new ApiFailure(StatusCodes.Status413PayloadTooLarge);
        }

        var buffer = new byte[MaxDocumentSize + 1];
        var read = await request.Body.ReadAtLeastAsync(buffer, buffer.Length, false, ct);
        if (read > MaxDocumentSize)
        {
            throw new ApiFailure(StatusCodes.Status413PayloadTooLarge);
        }

        var contentType = DetectContentType(buffer.AsSpan(0, read));
        if (contentType is null || !string.Equals(request.ContentType, contentType, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiFailure(StatusCodes.Status415UnsupportedMediaType);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockDraftAsync(hash, ct);
        if (await db.VerificationDocuments.CountAsync(document => document.DraftId == id, ct) >= 5)
        {
            throw new ApiFailure(StatusCodes.Status409Conflict);
        }

        var document = new VerificationDocument { DraftId = id, ContentType = contentType, Size = read, Content = buffer[..read] };
        db.VerificationDocuments.Add(document);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new VerificationDocumentInfo(document.Id, document.ContentType, document.Size);
    }

    internal async Task<ApplicationDraft> LockDraftAsync(string hash, CancellationToken ct)
    {
        var rows = await db.ApplicationDrafts.FromSql(
            $"SELECT * FROM \"ApplicationDrafts\" WHERE \"TokenHash\" = {hash} FOR UPDATE").ToListAsync(ct);
        var draft = rows.SingleOrDefault();
        if (draft is not { ConsumedAt: null } || draft.ExpiresAt <= clock.GetUtcNow())
        {
            throw new ApiFailure(StatusCodes.Status404NotFound);
        }

        return draft;
    }

    internal static string? DetectContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("%PDF-"u8)) return "application/pdf";
        if (bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return "image/jpeg";
        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return "image/png";
        return null;
    }
}
