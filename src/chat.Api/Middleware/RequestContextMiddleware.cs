namespace chat.Api.Middleware;

internal sealed partial class RequestContextMiddleware(RequestDelegate next, ILogger<RequestContextMiddleware> logger)
{
    public const string HeaderName = "X-Request-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[HeaderName];
        var requestId = supplied.Count == 1 && Guid.TryParseExact(supplied[0], "D", out var parsed)
            ? parsed.ToString("D")
            : Guid.NewGuid().ToString("D");
        context.TraceIdentifier = requestId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = requestId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = requestId }))
        {
            await next(context);
            RequestCompleted(logger, context.Response.StatusCode);
        }
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Request completed with status {StatusCode}")]
    private static partial void RequestCompleted(ILogger logger, int statusCode);
}
