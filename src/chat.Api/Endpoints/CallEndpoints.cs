using System.Security.Claims;
using chat.Api.Calls;
using chat.Api.Identity;
using chat.Contracts.Calls;

namespace chat.Api.Endpoints;

internal static class CallEndpoints
{
    public static void MapCallEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var calls = endpoints.MapGroup("/v1/calls").WithTags("Calls")
            .RequireAuthorization(AuthPrincipal.ParticipantPolicy);
        calls.MapPost("", async (CreateCallRequest request, ClaimsPrincipal actor, CallService service, CancellationToken ct) =>
            TypedResults.Ok(await service.CreateAsync(Actor(actor), request, ct)))
            .ValidateBody<CreateCallRequest>().WithName("CreateCall").WithApiErrors(404, 409, 503);
        calls.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal actor, CallService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ReadAsync(id, Actor(actor), ct)))
            .WithName("GetCall").WithApiErrors(404);
        calls.MapPost("/{id:guid}/end", async (Guid id, ClaimsPrincipal actor, CallService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ChangeAsync(id, Actor(actor), "end", ct)))
            .WithName("EndCall").WithApiErrors(404, 409, 503);
        calls.MapPost("/{id:guid}/ice-config", async (Guid id, ClaimsPrincipal actor, CallService service, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await service.IceAsync(id, Actor(actor), ct));
        }).WithName("GetCallIceConfig").WithApiErrors(404, 409, 503);
    }

    private static CallActor Actor(ClaimsPrincipal actor) => new(actor.UserId(), actor.FamilyId(), actor.Kind());
}

