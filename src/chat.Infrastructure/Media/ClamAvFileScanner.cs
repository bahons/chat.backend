using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using chat.Application.Media;

namespace chat.Infrastructure.Media;

public sealed class ClamAvFileScanner(string? host, int port) : IFileScanner
{
    public async Task<bool> IsCleanAsync(byte[] bytes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new IOException("ClamAV is unavailable.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, timeout.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token);
        var size = new byte[4];
        for (var offset = 0; offset < bytes.Length;)
        {
            var count = Math.Min(bytes.Length - offset, 64 * 1024);
            BinaryPrimitives.WriteInt32BigEndian(size, count);
            await stream.WriteAsync(size, timeout.Token);
            await stream.WriteAsync(bytes.AsMemory(offset, count), timeout.Token);
            offset += count;
        }

        Array.Clear(size);
        await stream.WriteAsync(size, timeout.Token);
        var response = new byte[1024];
        var received = 0;
        while (received < response.Length)
        {
            var length = await stream.ReadAsync(response.AsMemory(received), timeout.Token);
            if (length == 0)
            {
                break;
            }

            var terminator = Array.IndexOf(response, (byte)0, received, length);
            if (terminator >= 0)
            {
                var result = Encoding.UTF8.GetString(response, 0, terminator);
                if (result == "stream: OK")
                {
                    return true;
                }

                if (result.StartsWith("stream: ", StringComparison.Ordinal) && result.EndsWith(" FOUND", StringComparison.Ordinal))
                {
                    return false;
                }

                break;
            }

            received += length;
        }

        throw new IOException("ClamAV did not return a scan verdict.");
    }
}
