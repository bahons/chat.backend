using System.Security.Claims;
using chat.Api.Identity;
using chat.Api.Messaging;
using chat.Contracts.Messaging;

namespace chat.Api.Endpoints;

internal static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var devices = endpoints.MapGroup("/v1/devices").WithTags("Devices").RequireAuthorization(AuthPrincipal.ParticipantPolicy);
        devices.MapPost("", async (RegisterDeviceRequest request, ClaimsPrincipal actor, DeviceRegistry registry, CancellationToken ct) =>
            TypedResults.Ok(await registry.RegisterAsync(actor.UserId(), actor.FamilyId(), request.PushToken, ct)))
            .ValidateBody<RegisterDeviceRequest>().WithName("RegisterPushDevice").WithApiErrors(409);
        devices.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal actor, DeviceRegistry registry, CancellationToken ct) =>
        {
            await registry.ReleaseAsync(id, actor.UserId(), actor.FamilyId(), ct);
            return TypedResults.NoContent();
        }).WithName("RevokePushDevice").WithApiErrors(404);
    }
}
