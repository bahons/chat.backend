using Microsoft.AspNetCore.Diagnostics;

namespace chat.Api.Middleware;

internal sealed partial class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var statusCode = exception is BadHttpRequestException badRequest
            ? badRequest.StatusCode
            : StatusCodes.Status500InternalServerError;
        if (statusCode >= 500)
        {
            RequestFailed(logger, exception.GetType().Name);
        }

        await ApiErrors.WriteAsync(context, statusCode);
        return true;
    }

    [LoggerMessage(EventId = 101, Level = LogLevel.Error, Message = "Request failed ({ExceptionType})")]
    private static partial void RequestFailed(ILogger logger, string exceptionType);
}
