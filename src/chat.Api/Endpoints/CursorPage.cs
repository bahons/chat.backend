using System.Buffers.Binary;
using chat.Api.Middleware;
using chat.Contracts;
using Microsoft.IdentityModel.Tokens;

namespace chat.Api.Endpoints;

internal readonly record struct CursorPage(int Limit, DateTimeOffset? Before, Guid BeforeId, bool StableInbox = false)
{
    private const string InboxPrefix = "v2.";

    public static CursorPage ParseInbox(int? limit, string? cursor)
    {
        var stable = cursor is null || cursor.StartsWith(InboxPrefix, StringComparison.Ordinal);
        return Parse(limit, stable && cursor is not null ? cursor[InboxPrefix.Length..] : cursor)
            with
        { StableInbox = stable };
    }

    public static CursorPage Parse(int? requestedLimit, string? cursor)
    {
        var limit = requestedLimit ?? 20;
        if (limit is < 1 or > 100 || cursor is { Length: not 32 })
        {
            throw new ApiFailure(StatusCodes.Status400BadRequest);
        }

        if (cursor is null)
        {
            return new CursorPage(limit, null, default);
        }

        try
        {
            var bytes = Base64UrlEncoder.DecodeBytes(cursor);
            if (bytes.Length != 24)
            {
                throw new FormatException();
            }

            if (!string.Equals(Base64UrlEncoder.Encode(bytes), cursor, StringComparison.Ordinal))
            {
                throw new FormatException();
            }

            return new CursorPage(limit,
                new DateTimeOffset(BinaryPrimitives.ReadInt64BigEndian(bytes), TimeSpan.Zero), new Guid(bytes.AsSpan(8)));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentOutOfRangeException)
        {
            throw new ApiFailure(StatusCodes.Status400BadRequest);
        }
    }

    public PageResponse<T> MakePage<TRow, T>(IReadOnlyList<TRow> rows, Func<TRow, T> map,
        Func<TRow, DateTimeOffset> timestamp, Func<TRow, Guid> key)
    {
        var next = rows.Count > Limit
            ? (StableInbox ? InboxPrefix : "") + Encode(timestamp(rows[Limit - 1]), key(rows[Limit - 1]))
            : null;
        return new PageResponse<T>(rows.Take(Limit).Select(map).ToArray(), next);
    }

    private static string Encode(DateTimeOffset timestamp, Guid id)
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteInt64BigEndian(bytes, timestamp.UtcTicks);
        id.TryWriteBytes(bytes.AsSpan(8));
        return Base64UrlEncoder.Encode(bytes);
    }
}
