using chat.Api.Identity;
using chat.Api.Messaging;
using chat.Contracts.Messaging;
using Microsoft.AspNetCore.SignalR;

namespace chat.Api.Hubs;

internal sealed class MessagingHub(RealtimeConnections connections, RealtimeAccess access, TimeProvider clock) : Hub
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
}
