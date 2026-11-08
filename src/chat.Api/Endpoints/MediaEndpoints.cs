using System.Security.Claims;
using chat.Api.Identity;
using chat.Api.Messaging;
using chat.Contracts.Messaging;

namespace chat.Api.Endpoints;

internal static class MediaEndpoints
{
    public static void MapMediaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var media = endpoints.MapGroup("/v1").WithTags("Media").RequireAuthorization(AuthPrincipal.ParticipantPolicy);

        media.MapPost("/uploads/presign", async (PresignUploadRequest request, ClaimsPrincipal actor,
            MediaService service, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await service.PresignAsync(actor.UserId(), actor.FamilyId(), actor.Kind(), request, ct));
        }).ValidateBody<PresignUploadRequest>().WithName("PresignUpload").WithApiErrors(404, 503);

        media.MapPost("/uploads/{id:guid}/complete", async (Guid id, ClaimsPrincipal actor, MediaService service, CancellationToken ct) =>
            TypedResults.Ok(await service.CompleteAsync(id, actor.UserId(), actor.FamilyId(), actor.Kind(), ct)))
            .WithName("CompleteUpload").WithApiErrors(404, 409, 503);

        media.MapGet("/attachments/{id:guid}/download", async (Guid id, ClaimsPrincipal actor,
            MediaService service, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await service.DownloadAsync(id, actor.UserId(), actor.Kind(), ct));
        }).WithName("DownloadAttachment").WithApiErrors(404, 503);
    }
}
