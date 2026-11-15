using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using chat.Application.Media;

namespace chat.Infrastructure.Media;

public sealed class FfprobeMediaProbe(string executable) : IMediaProbe
{
    private readonly SemaphoreSlim slots = new(2, 2);

    public async Task<MediaMetadata?> InspectAsync(byte[] bytes, string contentType, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        if (!await slots.WaitAsync(TimeSpan.Zero, ct)) throw new IOException("Media probe is busy.");
        var path = Path.Combine(Path.GetTempPath(), $"chat-media-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(path, bytes, timeout.Token);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "-v", "quiet", "-threads", "1", "-max_alloc", "67108864",
                "-protocol_whitelist", "file", "-format_whitelist", "mov,matroska,webm,ogg,mp3",
                "-probesize", "5242880", "-analyzeduration", "5000000",
                "-show_entries", "format=format_name,duration:stream=codec_type,codec_name,width,height,duration",
                "-of", "json", path }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Media probe did not start.");
            try
            {
                var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
                var errors = ReadBoundedAsync(process.StandardError, timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var json = await output;
                await errors;
                if (process.ExitCode != 0) return null;
                return Parse(json, bytes, contentType);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        catch (System.ComponentModel.Win32Exception exception) { throw new IOException("Media probe is unavailable.", exception); }
        catch (JsonException) { return null; }
        catch (FormatException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("Media probe timed out."); }
        finally
        {
            try { File.Delete(path); } finally { slots.Release(); }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[65537];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(count), ct);
            if (read == 0) return new string(buffer, 0, count);
            count += read;
        }
        throw new IOException("Media probe output exceeded its limit.");
    }

    public static MediaMetadata? Parse(string json, ReadOnlySpan<byte> bytes, string contentType)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("format", out var format) || !format.TryGetProperty("format_name", out var name)
            || !root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array) return null;
        var mp4 = contentType.EndsWith("/mp4", StringComparison.Ordinal);
        var webm = contentType.EndsWith("/webm", StringComparison.Ordinal);
        var ogg = contentType == "audio/ogg";
        var mp3 = contentType == "audio/mpeg";
        var formatName = name.GetString() ?? "";
        var validContainer = mp4 ? bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8) && formatName.Contains("mp4", StringComparison.Ordinal)
            : webm ? bytes.Length >= 4 && bytes[..4].SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }) && formatName.Contains("webm", StringComparison.Ordinal)
            : ogg ? bytes.StartsWith("OggS"u8) && formatName == "ogg"
            : mp3 && formatName == "mp3";
        if (!validContainer) return null;
        string? audio = null, video = null;
        int? width = null, height = null;
        var duration = Duration(format);
        foreach (var stream in streams.EnumerateArray())
        {
            duration = Math.Max(duration, Duration(stream));
            if (!stream.TryGetProperty("codec_type", out var kind) || !stream.TryGetProperty("codec_name", out var codec)) return null;
            var codecName = codec.GetString();
            if (kind.GetString() == "audio" && audio is null && codecName is "aac" or "opus" or "vorbis" or "mp3") audio = codecName;
            else if (kind.GetString() == "video" && video is null && codecName is "h264" or "vp8" or "vp9" or "av1")
            {
                video = codecName;
                if (!stream.TryGetProperty("width", out var w) || !stream.TryGetProperty("height", out var h)) return null;
                width = w.GetInt32(); height = h.GetInt32();
                if (width is < 1 or > 1920 || height is < 1 or > 1920 || (long)width * height > 1920 * 1080) return null;
            }
            else return null;
        }
        var isAudio = contentType.StartsWith("audio/", StringComparison.Ordinal);
        if (!double.IsFinite(duration) || duration <= 0 || duration > (isAudio ? 300 : 120)
            || (isAudio ? audio is null || video is not null : video is null)) return null;
        if (mp4 && (audio is not (null or "aac") || video is not (null or "h264"))) return null;
        if (webm && (audio is not (null or "opus" or "vorbis") || video is not (null or "vp8" or "vp9" or "av1"))) return null;
        if (ogg && audio is not ("opus" or "vorbis")) return null;
        if (mp3 && audio != "mp3") return null;
        return new MediaMetadata((int)Math.Ceiling(duration * 1000), width, height, audio, video);
    }

    private static double Duration(JsonElement element) =>
        element.TryGetProperty("duration", out var duration)
        && double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds : 0;
}

