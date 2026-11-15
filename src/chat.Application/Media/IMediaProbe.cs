namespace chat.Application.Media;

public sealed record MediaMetadata(int DurationMilliseconds, int? Width, int? Height, string? AudioCodec, string? VideoCodec);
public interface IMediaProbe
{
    Task<MediaMetadata?> InspectAsync(byte[] bytes, string contentType, CancellationToken ct);
}

