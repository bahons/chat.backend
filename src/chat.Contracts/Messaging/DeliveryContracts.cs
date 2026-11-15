using System.ComponentModel.DataAnnotations;

namespace chat.Contracts.Messaging;

public sealed record PresignUploadRequest(Guid ConversationId,
    [property: Required, RegularExpression("^(image/jpeg|image/png|application/pdf|audio/mp4|audio/webm|audio/ogg|audio/mpeg|video/mp4|video/webm)$")] string ContentType,
    [property: Range(1, 50 * 1024 * 1024)] int Size);

public sealed record PresignedUpload(Guid UploadId, string UploadUrl, DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, string> Headers);
public sealed record CompletedUpload(Guid UploadId, string Status);
public sealed record AttachmentDownload(string DownloadUrl, DateTimeOffset ExpiresAt);
public sealed record RegisterDeviceRequest(
    [property: Required, StringLength(128), RegularExpression(@"^(ExponentPushToken|ExpoPushToken)\[[A-Za-z0-9_-]+\]$")] string PushToken);
public sealed record RegisteredDevice(Guid Id);
