using chat.Api.Identity;
using chat.Api.Calls;
using chat.Api.Middleware;
using chat.Contracts.Calls;
using System.ComponentModel.DataAnnotations;
using chat.Api.Messaging;
using chat.Contracts.Messaging;
using Microsoft.AspNetCore.SignalR;

namespace chat.Api.Hubs;

internal sealed class MessagingHub(RealtimeConnections connections, RealtimeAccess access, TimeProvider clock,
    CallService calls, CallDelivery delivery) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var caller = Context;
        var principal = caller.User!;
        var connection = new LiveConnection(caller.ConnectionId, principal.UserId(), principal.FamilyId(),
            principal.Kind(), caller.Abort);
        if (!await access.InitializeAsync(connection, caller.ConnectionAborted) || !connections.TryAdd(connection))
        {
            caller.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public async Task JoinConversation(string conversationId)
    {
        var (id, connection) = await AuthorizeAsync(conversationId);
        if (!connection.Conversations.ContainsKey(id) && connection.Conversations.Count >= RealtimeConnections.SubscriptionLimit)
        {
            throw new HubException("Subscription limit reached.");
        }

        connection.Conversations.TryAdd(id, 0);
    }

    public async Task LeaveConversation(string conversationId)
    {
        var (id, connection) = await AuthorizeAsync(conversationId);
        connection.Conversations.TryRemove(id, out _);
    }

    public async Task SetTyping(string conversationId, bool isTyping)
    {
        var (id, connection) = await AuthorizeAsync(conversationId);
        var now = clock.GetUtcNow();
        if (!connection.Conversations.ContainsKey(id) || !connection.AllowTyping(now))
        {
            return;
        }

        var ids = await access.RecipientsAsync(id, connections.Snapshot.Where(item => item.Id != connection.Id).ToArray(),
            Context.ConnectionAborted);
        await Clients.Clients(ids).SendAsync(Outbox.TypingChanged, new EventEnvelope<object>(Guid.NewGuid(), 1, now, id,
            new { conversationId = id, side = ConversationAccess.SideOf(connection.Kind).ToString(), isTyping }),
            Context.ConnectionAborted);
    }

    private async Task<(Guid Id, LiveConnection Connection)> AuthorizeAsync(string conversationId)
    {
        var connection = connections.Find(Context.ConnectionId);
        if (connection is null || !await access.CanConnectAsync(connection, Context.ConnectionAborted))
        {
            Context.Abort();
            throw new HubException("Authentication required.");
        }

        if (!Guid.TryParse(conversationId, out var id) || !await access.CanReadAsync(id, connection, Context.ConnectionAborted))
        {
            if (connection.Conversations.ContainsKey(id))
            {
                Context.Abort();
            }

            throw new HubException("Conversation unavailable.");
        }

        return (id, connection);
    }

    public Task AcceptCall(string callId) => ChangeCallAsync(callId, "accept");
    public Task RejectCall(string callId, string? reason = null) =>
        reason is { Length: > 64 } ? Task.FromException(new HubException("Invalid call request.")) : ChangeCallAsync(callId, "reject");
    public Task HangupCall(string callId) => ChangeCallAsync(callId, "end");

    public Task SendOffer(string callId, string sdp) => SignalAsync(callId, "SendOffer", sdp, null);
    public Task SendAnswer(string callId, string sdp) => SignalAsync(callId, "SendAnswer", sdp, null);
    public Task SendIceCandidate(string callId, IceCandidate candidate) => SignalAsync(callId, "SendIceCandidate", null, candidate);

    private async Task ChangeCallAsync(string callId, string action)
    {
        var (id, actor) = await CallActorAsync(callId);
        try { await calls.ChangeAsync(id, actor, action, Context.ConnectionAborted); }
        catch (ApiFailure failure) { throw CallError(failure); }
    }

    private async Task SignalAsync(string callId, string method, string? sdp, IceCandidate? candidate)
    {
        if (method == "SendIceCandidate")
        {
            if (candidate is null || !Validator.TryValidateObject(candidate, new ValidationContext(candidate), null, true)
                || !candidate.Candidate.StartsWith("candidate:", StringComparison.Ordinal) || candidate.Candidate.Any(char.IsControl))
                throw new HubException("Invalid call request.");
        }
        else if (sdp is null || sdp.Length is < 5 or > 24576 || System.Text.Encoding.UTF8.GetByteCount(sdp) > 24576
            || !sdp.StartsWith("v=0", StringComparison.Ordinal)
            || sdp.Contains('\0'))
            throw new HubException("Invalid call request.");
        var (id, actor) = await CallActorAsync(callId);
        try
        {
            var call = await calls.SignalAsync(id, actor, method, Context.ConnectionAborted);
            var name = method switch { "SendOffer" => "CallOffer", "SendAnswer" => "CallAnswer", _ => "CallIceCandidate" };
            object payload = candidate is null ? new CallSdpEvent(id, sdp!, call.Revision) : new CallIceEvent(id, candidate, call.Revision);
            await delivery.RelayAsync(call, actor, name, payload, Context.ConnectionAborted);
        }
        catch (ApiFailure failure) { throw CallError(failure); }
    }

    private async Task<(Guid Id, CallActor Actor)> CallActorAsync(string callId)
    {
        if (!Guid.TryParse(callId, out var id)) throw new HubException("Invalid call request.");
        var connection = connections.Find(Context.ConnectionId);
        if (connection is null || !await access.CanConnectAsync(connection, Context.ConnectionAborted))
        {
            Context.Abort();
            throw new HubException("Authentication required.");
        }
        if (!connection.AllowCall(clock.GetUtcNow())) throw new HubException("Call rate limit exceeded.");
        return (id, new CallActor(connection.UserId, connection.FamilyId, connection.Kind));
    }

    private static HubException CallError(ApiFailure failure) => new(failure.StatusCode switch
    {
        404 or 401 => "Call unavailable.",
        429 => "Call rate limit exceeded.",
        503 => "Call service unavailable.",
        _ => "Call state conflict."
    });
}
