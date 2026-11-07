using chat.Contracts;

namespace chat.Api.Middleware;

internal static class ApiErrors
{
    public static Task WriteAsync(HttpContext context, int statusCode)
    {
        var (code, message) = Describe(statusCode);
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(
            new ErrorEnvelope(new ApiError(code, message, context.TraceIdentifier)), context.RequestAborted);
    }

    internal static (string Code, string Message) Describe(int statusCode) => statusCode switch
    {
        400 or 405 or 406 or 413 or 415 or 422 => ("VALIDATION_ERROR", "Invalid request."),
        401 => ("UNAUTHENTICATED", "Authentication is required."),
        403 => ("FORBIDDEN", "Access is forbidden."),
        404 => ("NOT_FOUND", "Resource not found."),
        409 => ("CONFLICT", "The request conflicts with the current state."),
        429 => ("RATE_LIMITED", "Too many requests."),
        >= 400 and < 500 => ("VALIDATION_ERROR", "Request rejected."),
        _ => ("INTERNAL_ERROR", "The service could not complete the request.")
    };
}
