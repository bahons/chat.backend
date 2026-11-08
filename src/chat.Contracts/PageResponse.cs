namespace chat.Contracts;

public sealed record PageResponse<T>(IReadOnlyList<T> Items, string? NextCursor);
