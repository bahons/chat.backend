namespace chat.Contracts;

public sealed record ErrorEnvelope(ApiError Error);

public sealed record ApiError(string Code, string Message, string RequestId, object? Details = null);
