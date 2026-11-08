using System.Security.Claims;
using chat.Api.Businesses;
using chat.Api.Identity;
using chat.Contracts;
using chat.Contracts.Businesses;

namespace chat.Api.Endpoints;

internal static class BusinessEndpoints
{
    public static void MapBusinessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var drafts = endpoints.MapGroup("/v1/business-applications").WithTags("Business verification")
            .RequireRateLimiting(IdentityServices.RatePolicy).AllowAnonymous();
        drafts.MapPost("/drafts", async (BusinessDrafts service, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await service.CreateAsync(ct));
        }).WithName("CreateVerificationDraft");

        drafts.MapPost("/drafts/{id:guid}/documents", async (Guid id, BusinessDrafts service, HttpRequest request, CancellationToken ct) =>
            TypedResults.Ok(await service.UploadAsync(id, request.Headers[BusinessDrafts.TokenHeader].ToString(), request, ct)))
            .Accepts<byte[]>("application/pdf", "image/jpeg", "image/png").WithName("UploadVerificationDocument").WithApiErrors(404, 409, 413, 415);

        drafts.MapPost("", async (SubmitBusinessRequest request, BusinessService service, CancellationToken ct) =>
            TypedResults.Ok(await service.SubmitAsync(request, ct)))
            .ValidateBody<SubmitBusinessRequest>().WithName("SubmitBusinessApplication").WithApiErrors(404, 409, 503);

        var own = endpoints.MapGroup("/v1/my-business").WithTags("Business verification")
            .RequireAuthorization(AuthPrincipal.BusinessPolicy);
        own.MapPost("/{id:guid}/applications", async (Guid id, ReapplyBusinessRequest request, ClaimsPrincipal principal,
            BusinessService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ReapplyAsync(principal.UserId(), id, request, ct)))
            .ValidateBody<ReapplyBusinessRequest>().RequireRateLimiting(IdentityServices.RatePolicy)
            .WithName("ReapplyBusinessApplication").WithApiErrors(404, 409);
        own.MapGet("", async (Guid? businessId, ClaimsPrincipal principal, BusinessService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetProfileAsync(principal.UserId(), businessId, ct))).WithName("GetMyBusiness").WithApiErrors(404);
        own.MapPatch("", async (Guid? businessId, UpdateBusinessRequest request, ClaimsPrincipal principal, BusinessService service, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateProfileAsync(principal.UserId(), businessId, request, ct)))
            .ValidateBody<UpdateBusinessRequest>().WithName("UpdateMyBusiness").WithApiErrors(404);

        var admin = endpoints.MapGroup("/v1/admin").WithTags("Business moderation").RequireAuthorization(AuthPrincipal.AdminPolicy);
        admin.MapGet("/business-applications", async (string? status, int? limit, string? cursor, BusinessService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(status, CursorPage.Parse(limit, cursor), ct))).WithName("ListBusinessApplications");
        admin.MapGet("/business-applications/{id:guid}", async (Guid id, BusinessService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetApplicationAsync(id, ct))).WithName("GetBusinessApplication").WithApiErrors(404);
        admin.MapGet("/business-applications/{id:guid}/documents/{documentId:guid}", async
            (Guid id, Guid documentId, BusinessService service, HttpContext context, CancellationToken ct) =>
        {
            var document = await service.GetDocumentAsync(id, documentId, ct);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return TypedResults.File(document.Content, "application/octet-stream", document.ContentType switch
            {
                "application/pdf" => "verification.pdf",
                "image/png" => "verification.png",
                _ => "verification.jpg"
            });
        }).WithName("DownloadVerificationDocument").Produces<ErrorEnvelope>(StatusCodes.Status404NotFound);
        admin.MapPost("/business-applications/{id:guid}/decision", async
            (Guid id, DecisionRequest request, ClaimsPrincipal principal, BusinessService service, CancellationToken ct) =>
            TypedResults.Ok(await service.DecideAsync(principal.UserId(), id, request, ct)))
            .ValidateBody<DecisionRequest>().WithName("DecideBusinessApplication").WithApiErrors(404, 409);
        admin.MapPost("/businesses/{id:guid}/suspend", async
            (Guid id, SuspendBusinessRequest request, ClaimsPrincipal principal, BusinessService service, CancellationToken ct) =>
        {
            await service.SuspendAsync(principal.UserId(), id, request.Reason, ct);
            return TypedResults.NoContent();
        }).ValidateBody<SuspendBusinessRequest>().WithName("SuspendBusiness").WithApiErrors(404, 409);
    }
}
