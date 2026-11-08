using System.Security.Claims;
using chat.Api.Identity;
using chat.Api.Messaging;
using chat.Contracts.Messaging;

namespace chat.Api.Endpoints;

internal static class ConversationEndpoints
{
    public static void MapConversationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var chats = endpoints.MapGroup("/v1/conversations").WithTags("Messaging")
            .RequireAuthorization(AuthPrincipal.ParticipantPolicy);
        chats.MapPost("", async (CreateConversationRequest request, ClaimsPrincipal actor, ConversationService service, CancellationToken ct) =>
            TypedResults.Ok(await service.CreateAsync(actor.UserId(), request.BusinessId, ct)))
            .RequireAuthorization(AuthPrincipal.ClientPolicy).WithName("CreateConversation").WithApiErrors(404);
        chats.MapGet("", async (string? cursor, int? limit, ClaimsPrincipal actor, ConversationService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(actor.UserId(), actor.Kind(), CursorPage.ParseInbox(limit, cursor), ct)))
            .WithName("ListConversations");
        chats.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal actor, ConversationService service, CancellationToken ct) =>
            TypedResults.Ok(await service.DetailAsync(id, actor.UserId(), actor.Kind(), ct)))
            .WithName("GetConversation").WithApiErrors(404);
        chats.MapGet("/{id:guid}/messages", async (Guid id, string? cursor, int? limit, ClaimsPrincipal actor,
            ConversationService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListMessagesAsync(id, actor.UserId(), actor.Kind(), CursorPage.Parse(limit, cursor), ct)))
            .WithName("ListMessages").WithApiErrors(404);
        chats.MapPost("/{id:guid}/messages", async (Guid id, SendMessageRequest request, ClaimsPrincipal actor,
            ConversationService service, CancellationToken ct) =>
            TypedResults.Ok(await service.SendAsync(id, actor.UserId(), actor.Kind(), request, ct)))
            .ValidateBody<SendMessageRequest>().WithName("SendMessage").WithApiErrors(404, 409, 503);
        chats.MapPost("/{id:guid}/read", async (Guid id, ReadConversationRequest request, ClaimsPrincipal actor,
            ConversationService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ReadAsync(id, actor.UserId(), actor.Kind(), request.LastMessageId, ct)))
            .WithName("MarkConversationRead").WithApiErrors(404, 503);
    }
}
