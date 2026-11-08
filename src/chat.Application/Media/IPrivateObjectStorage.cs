namespace chat.Application.Media;

public interface IPrivateObjectStorage
{
    Task<string> PresignUploadAsync(string key, string contentType, int size, DateTimeOffset expiresAt, CancellationToken ct);
    Task<byte[]?> ReadUploadAsync(string key, string contentType, int size, CancellationToken ct);
    Task SaveVerifiedAsync(string key, byte[] bytes, string contentType, CancellationToken ct);
    Task<string> PresignDownloadAsync(string key, DateTimeOffset expiresAt, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}

public interface IFileScanner
{
    Task<bool> IsCleanAsync(byte[] bytes, CancellationToken ct);
}
