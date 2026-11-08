using System.Security.Claims;
using chat.Api.Identity;
using chat.Contracts;
using chat.Contracts.Identity;
using Microsoft.AspNetCore.Http.HttpResults;

namespace chat.Api.Endpoints;

internal static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/v1/auth").WithTags("Auth").RequireRateLimiting(IdentityServices.RatePolicy);

        auth.MapPost("/client/register", async (RegisterClientRequest request, AccountService account, CancellationToken ct) =>
        {
            await account.RegisterClientAsync(request, ct);
            return TypedResults.Accepted((string?)null);
        }).AllowAnonymous().ValidateBody<RegisterClientRequest>().WithName("RegisterClient").WithApiErrors(503)
            .Produces<ErrorEnvelope>(StatusCodes.Status400BadRequest);

        auth.MapPost("/login", async Task<Results<Ok<AuthTokens>, StatusCodeHttpResult>> (LoginRequest request, AccountService account, LoginAttemptLimiter limiter, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var retry = limiter.Admit(request.Email, context.Connection.RemoteIpAddress);
            if (retry > 0)
            {
                LoginAttemptLimiter.SetRetryAfter(context, retry);
                return TypedResults.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            return TypedResults.Ok(await account.LoginAsync(request, ct));
        }).AllowAnonymous().ValidateBody<LoginRequest>().WithName("Login").WithApiErrors(503)
            .Produces<ErrorEnvelope>(StatusCodes.Status400BadRequest)
            .Produces<ErrorEnvelope>(StatusCodes.Status401Unauthorized)
            .Produces<ErrorEnvelope>(StatusCodes.Status403Forbidden);

        auth.MapPost("/refresh", async (RefreshRequest request, AccountService account, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await account.RefreshAsync(request.RefreshToken, ct));
        }).AllowAnonymous().ValidateBody<RefreshRequest>().WithName("RefreshSession").WithApiErrors(503)
            .Produces<ErrorEnvelope>(StatusCodes.Status400BadRequest)
            .Produces<ErrorEnvelope>(StatusCodes.Status401Unauthorized);

        auth.MapPost("/logout", async (ClaimsPrincipal principal, AccountService account, CancellationToken ct) =>
        {
            await account.LogoutAsync(principal.UserId(), principal.FamilyId(), ct);
            return TypedResults.NoContent();
        }).RequireAuthorization().WithName("Logout").WithApiErrors(503).Produces<ErrorEnvelope>(StatusCodes.Status401Unauthorized);

        auth.MapPost("/email/verify", async (VerifyEmailRequest request, AccountService account, CancellationToken ct) =>
        {
            await account.VerifyAsync(request.Token, ct);
            return TypedResults.NoContent();
        }).AllowAnonymous().ValidateBody<VerifyEmailRequest>().WithName("VerifyEmail")
            .Produces<ErrorEnvelope>(StatusCodes.Status400BadRequest);

        auth.MapPost("/password/forgot", async (ForgotPasswordRequest request, AccountService account, CancellationToken ct) =>
        {
            await account.ForgotAsync(request.Email, ct);
            return TypedResults.Accepted((string?)null);
        }).AllowAnonymous().ValidateBody<ForgotPasswordRequest>().WithName("ForgotPassword").WithApiErrors(503)
            .Produces<ErrorEnvelope>(StatusCodes.Status400BadRequest);

        auth.MapPost("/password/reset", async (ResetPasswordRequest request, AccountService account, CancellationToken ct) =>
        {
            await account.ResetAsync(request, ct);
            return TypedResults.NoContent();
        }).AllowAnonymous().ValidateBody<ResetPasswordRequest>().WithName("ResetPassword").WithApiErrors(503)
            .Produces<ErrorEnvelope>(StatusCodes.Status400BadRequest);

        endpoints.MapGet("/v1/me", async (ClaimsPrincipal principal, AccountService account, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await account.MeAsync(principal.UserId(), ct));
        }).RequireAuthorization().WithTags("Auth").WithName("GetMe")
            .Produces<ErrorEnvelope>(StatusCodes.Status401Unauthorized);
    }
}
