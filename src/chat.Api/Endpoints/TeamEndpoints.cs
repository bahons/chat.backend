using System.Security.Claims;
using chat.Api.Businesses;
using chat.Api.Identity;
using chat.Contracts.Businesses;

namespace chat.Api.Endpoints;

internal static class TeamEndpoints
{
    public static void MapTeamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var team = endpoints.MapGroup("/v1").WithTags("Team and analytics").RequireAuthorization(AuthPrincipal.BusinessPolicy);
        team.MapGet("/team", async (Guid? businessId, ClaimsPrincipal principal, TeamService service, CancellationToken ct) =>
            TypedResults.Ok(await service.MembersAsync(principal.UserId(), businessId, ct)))
            .WithName("GetTeam").WithApiErrors(404);
        team.MapGet("/my-business/members", async (Guid? businessId, ClaimsPrincipal principal, TeamService service, CancellationToken ct) =>
            TypedResults.Ok(await service.MembersAsync(principal.UserId(), businessId, ct)))
            .WithName("ListBusinessMembers").WithApiErrors(404);
        team.MapPatch("/my-business/members/{id:guid}", async (Guid id, Guid? businessId, UpdateMemberRequest request,
            ClaimsPrincipal principal, TeamService service, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateAsync(principal.UserId(), businessId, id, request, ct)))
            .ValidateBody<UpdateMemberRequest>().WithName("UpdateBusinessMember").WithApiErrors(404, 409, 503);
        team.MapGet("/my-business/invitations", async (Guid? businessId, ClaimsPrincipal principal, TeamService service, CancellationToken ct) =>
            TypedResults.Ok(await service.InvitationsAsync(principal.UserId(), businessId, ct)))
            .WithName("ListTeamInvitations").WithApiErrors(404);
        team.MapPost("/my-business/invitations", async (Guid? businessId, InviteTeamRequest request, HttpContext context,
            TeamService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await service.InviteAsync(context.User.UserId(), businessId, request, ct));
        }).ValidateBody<InviteTeamRequest>().WithName("InviteTeamMember").WithApiErrors(404);
        team.MapDelete("/my-business/invitations/{id:guid}", async (Guid id, Guid? businessId, ClaimsPrincipal principal,
            TeamService service, CancellationToken ct) =>
        {
            await service.RevokeAsync(principal.UserId(), businessId, id, ct);
            return TypedResults.NoContent();
        }).WithName("RevokeTeamInvitation").WithApiErrors(404, 409);
        // Optional authenticated identity is checked by the service for existing accounts.
        endpoints.MapPost("/v1/invitations/accept", async (AcceptInvitationRequest request, HttpContext context,
            TeamService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await service.AcceptAsync(context.User, request, ct));
        }).AllowAnonymous().WithTags("Team and analytics").ValidateBody<AcceptInvitationRequest>()
            .RequireRateLimiting(IdentityServices.RatePolicy).WithName("AcceptTeamInvitation").WithApiErrors(401, 404, 409, 503);
        team.MapGet("/conversations/{id:guid}/assignment", async (Guid id, ClaimsPrincipal principal, TeamService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetAssignmentAsync(principal.UserId(), id, ct)))
            .WithName("GetConversationAssignment").WithApiErrors(404);
        team.MapPatch("/conversations/{id:guid}/assignment", async (Guid id, AssignmentRequest request, ClaimsPrincipal principal,
            TeamService service, CancellationToken ct) =>
            TypedResults.Ok(await service.AssignAsync(principal.UserId(), id, request, ct)))
            .ValidateBody<AssignmentRequest>().WithName("AssignConversation").WithApiErrors(404, 409, 503);
        team.MapGet("/my-business/analytics", async (Guid? businessId, DateTimeOffset? from, DateTimeOffset? to,
            ClaimsPrincipal principal, TeamService service, CancellationToken ct) =>
            TypedResults.Ok(await service.AnalyticsAsync(principal.UserId(), businessId, from, to, ct)))
            .WithName("GetBusinessAnalytics").WithApiErrors(404);
    }
}
