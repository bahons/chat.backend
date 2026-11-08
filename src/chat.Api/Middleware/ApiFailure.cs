namespace chat.Api.Middleware;

internal sealed class ApiFailure(int statusCode, string? code = null, string? publicMessage = null) : Exception
{
    public int StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
    public string? PublicMessage { get; } = publicMessage;
}
