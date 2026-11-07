using chat.Api.Middleware;
using chat.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace chat.Api.Endpoints;

internal static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var health = endpoints.MapGroup("/health").WithTags("Health").AllowAnonymous().DisableRateLimiting();

        health.MapGet("/live", (HttpContext context) => Healthy(context))
            .WithName("HealthLive");

        health.MapGet("/ready", CheckReadinessAsync)
            .WithName("HealthReady")
            .Produces<HealthResponse>()
            .Produces<ErrorEnvelope>(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task CheckReadinessAsync(HttpContext context, HealthCheckService checks)
    {
        context.Response.Headers.CacheControl = "no-store";
        var report = await checks.CheckHealthAsync(check => check.Tags.Contains("ready"), context.RequestAborted);
        if (report.Status != HealthStatus.Healthy)
        {
            await ApiErrors.WriteAsync(context, StatusCodes.Status503ServiceUnavailable);
            return;
        }

        await Healthy(context).ExecuteAsync(context);
    }

    private static Ok<HealthResponse> Healthy(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new HealthResponse("healthy"));
    }
}
