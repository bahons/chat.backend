using System.Globalization;
using System.Security.Cryptography;
using Amazon.S3;
using chat.Api.Businesses;
using chat.Api.Identity;
using chat.Api.Middleware;
using chat.Application.Media;
using chat.Contracts.Messaging;
using chat.Domain.Identity;
using chat.Domain.Messaging;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace chat.Api.Messaging;

internal sealed class MediaService(ChatDbContext db, ConversationAccess access, IPrivateObjectStorage storage,
    IFileScanner scanner, TimeProvider clock)
{
    public async Task<PresignedUpload> PresignAsync(Guid actor, Guid family, UserKind kind, PresignUploadRequest request, CancellationToken ct)
    {
        await access.RequireAsync(request.ConversationId, actor, kind, ct);
        var upload = new Attachment
        {
            ConversationId = request.ConversationId,
            UploaderUserId = actor,
            UploadFamilyId = family,
            ContentType = request.ContentType,
            Size = request.Size,
            UploadExpiresAt = clock.GetUtcNow().AddMinutes(5)
        };
        var url = await storage.PresignUploadAsync(StagingKey(upload.Id), upload.ContentType, upload.Size, upload.UploadExpiresAt, ct);
        db.Attachments.Add(upload);
        await db.SaveChangesAsync(ct);
        return new PresignedUpload(upload.Id, url, upload.UploadExpiresAt, new Dictionary<string, string>
        {
            ["Content-Type"] = upload.ContentType,
            ["Content-Length"] = upload.Size.ToString(CultureInfo.InvariantCulture)
        });
    }

    public async Task<CompletedUpload> CompleteAsync(Guid id, Guid actor, Guid family, UserKind kind, CancellationToken ct)
    {
        var visible = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(
            upload => upload.Id == id && upload.UploaderUserId == actor && upload.RetiredAt == null, ct) ?? throw NotFound();
        await access.RequireAsync(visible.ConversationId, actor, kind, ct);
        if (visible.UploadFamilyId is { } originalFamily && originalFamily != family)
        {
            throw NotFound();
        }

        if (visible.Status != AttachmentStatus.PENDING)
        {
            return new CompletedUpload(id, visible.Status.ToString());
        }

        if (visible.UploadFamilyId is null || visible.UploadExpiresAt.AddMinutes(10) <= clock.GetUtcNow())
        {
            throw new ApiFailure(StatusCodes.Status409Conflict, "MEDIA_NOT_READY", "Upload expired.");
        }

        var bytes = await StorageOperationAsync(token => storage.ReadUploadAsync(StagingKey(id), visible.ContentType, visible.Size, token), ct)
            ?? throw new ApiFailure(StatusCodes.Status409Conflict, "MEDIA_NOT_READY", "Upload is missing or has incorrect metadata.");
        var clean = BusinessDrafts.DetectContentType(bytes) == visible.ContentType
            && (visible.ContentType != "application/pdf" || VerificationDocumentScanner.HasPdfEnvelope(bytes))
            && await ScanAsync(bytes, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await access.LockAsync(visible.ConversationId, actor, kind, ct);
        var upload = await db.Attachments.SingleAsync(row => row.Id == id, ct);
        var user = (await db.Users.FromSql($"SELECT * FROM \"Users\" WHERE \"Id\" = {actor} FOR SHARE").ToListAsync(ct)).SingleOrDefault();
        if (upload.RetiredAt != null || user is not { IsActive: true, EmailVerifiedAt: not null } || user.Kind != kind
            || !await AuthPrincipal.ActiveSessions(db, clock.GetUtcNow()).AnyAsync(
                session => session.UserId == actor && session.FamilyId == family, ct))
        {
            throw NotFound();
        }

        if (upload.Status == AttachmentStatus.PENDING)
        {
            if (upload.UploadExpiresAt.AddMinutes(10) <= clock.GetUtcNow())
            {
                throw new ApiFailure(StatusCodes.Status409Conflict, "MEDIA_NOT_READY", "Upload expired.");
            }

            if (clean)
            {
                await StorageOperationAsync(async token =>
                {
                    await storage.SaveVerifiedAsync(VerifiedKey(id), bytes, upload.ContentType, token);
                    return true;
                }, ct);
                upload.Checksum = Convert.ToHexString(SHA256.HashData(bytes));
                upload.Status = AttachmentStatus.READY;
            }
            else
            {
                upload.Status = AttachmentStatus.QUARANTINED;
            }

            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new CompletedUpload(id, upload.Status.ToString());
    }

    public async Task<AttachmentDownload> DownloadAsync(Guid id, Guid actor, UserKind kind, CancellationToken ct)
    {
        var permitted = await db.Attachments.AsNoTracking().AnyAsync(upload => upload.Id == id
            && upload.MessageId != null && upload.Status == AttachmentStatus.READY
            && access.VisibleTo(actor, kind).Any(conversation => conversation.Id == upload.ConversationId), ct);
        if (!permitted)
        {
            throw NotFound();
        }

        var expires = clock.GetUtcNow().AddMinutes(1);
        return new AttachmentDownload(await storage.PresignDownloadAsync(VerifiedKey(id), expires, ct), expires);
    }

    private async Task<bool> ScanAsync(byte[] bytes, CancellationToken ct)
    {
        try
        {
            return await scanner.IsCleanAsync(bytes, ct);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested &&
            exception is IOException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            throw new ApiFailure(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<T> StorageOperationAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return await operation(timeout.Token);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested &&
            exception is AmazonS3Exception or IOException or HttpRequestException or OperationCanceledException)
        {
            throw new ApiFailure(StatusCodes.Status503ServiceUnavailable);
        }
    }

    internal static string StagingKey(Guid id) => $"incoming/{id:N}";
    internal static string VerifiedKey(Guid id) => $"media/{id:N}";
    private static ApiFailure NotFound() => new(StatusCodes.Status404NotFound);
}
