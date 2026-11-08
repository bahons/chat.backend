using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Threading.Channels;
using chat.Application.Media;
using chat.Application.Push;
using chat.Contracts.Messaging;

namespace chat.IntegrationTests;

public sealed class TestObjectStorage : IPrivateObjectStorage
{
    private readonly ConcurrentDictionary<string, byte[]> objects = new();
    public Func<string, bool>? FailDelete { get; set; }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        if (FailDelete?.Invoke(key) == true)
        {
            throw new IOException("Simulated object-store deletion failure.");
        }

        objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<string> PresignUploadAsync(string key, string contentType, int size, DateTimeOffset expiresAt, CancellationToken ct) =>
        Task.FromResult($"https://storage.example.test/{key}");

    public Task<byte[]?> ReadUploadAsync(string key, string contentType, int size, CancellationToken ct) =>
        Task.FromResult(objects.TryGetValue(key, out var content) && content.Length == size ? content : null);

    public Task SaveVerifiedAsync(string key, byte[] bytes, string contentType, CancellationToken ct)
    {
        objects[key] = bytes;
        return Task.CompletedTask;
    }

    public Task<string> PresignDownloadAsync(string key, DateTimeOffset expiresAt, CancellationToken ct) =>
        Task.FromResult($"https://storage.example.test/{key}");

    public async Task UploadAsync(PresignedUpload upload, byte[] bytes, string contentType)
    {
        var uri = new Uri(upload.UploadUrl);
        if (uri.Host == "storage.example.test")
        {
            objects[uri.AbsolutePath.TrimStart('/')] = bytes;
            return;
        }

        using var client = new HttpClient();
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await client.PutAsync(uri, content);
        response.EnsureSuccessStatusCode();
    }

    public async Task<byte[]> DownloadAsync(AttachmentDownload download)
    {
        var uri = new Uri(download.DownloadUrl);
        if (uri.Host == "storage.example.test")
        {
            return objects[uri.AbsolutePath.TrimStart('/')];
        }

        using var client = new HttpClient();
        return await client.GetByteArrayAsync(uri);
    }
}

public sealed class TestFileScanner : IFileScanner
{
    public Task<bool> IsCleanAsync(byte[] bytes, CancellationToken ct)
    {
        var content = bytes.AsSpan();
        if (content.IndexOf("scan-unavailable"u8) >= 0)
        {
            throw new IOException("Test scanner unavailable.");
        }

        return Task.FromResult(content.IndexOf("scan-denied"u8) < 0);
    }
}

public sealed class TestPushGateway : IPushGateway
{
    public Channel<(string Token, Guid EventId, Guid ConversationId)> Sends { get; } = Channel.CreateUnbounded<(string, Guid, Guid)>();
    private int sendCount;
    public Func<CancellationToken, Task>? BeforeResult { get; set; }

    public async Task<PushResult> SendAsync(string token, Guid eventId, Guid conversationId, CancellationToken ct)
    {
        await Sends.Writer.WriteAsync((token, eventId, conversationId), ct);
        if (BeforeResult is { } beforeResult)
        {
            await beforeResult(ct);
        }
        return Interlocked.Increment(ref sendCount) == 1
            ? new PushResult(PushResultKind.Retry)
            : new PushResult(PushResultKind.Accepted, "test-ticket");
    }

    public Task<PushResult> CheckAsync(string ticketId, CancellationToken ct) =>
        Task.FromResult(new PushResult(PushResultKind.Delivered));
}
