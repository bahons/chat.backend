using System.Text.Json;
using chat.Api.Hubs;
using chat.Api.Identity;
using chat.Api.Messaging;
using chat.Contracts.Messaging;
using chat.Domain.Calls;
using chat.Domain.Identity;
using chat.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace chat.Api.Calls;

internal sealed class CallDelivery(ChatDbContext db, RealtimeConnections connections, RealtimeAccess access,
    IHubContext<MessagingHub> hub, TimeProvider clock)
{
    public async Task<string> StateAsync(string name, Guid conversationId, JsonElement payload, Guid eventId,
        DateTimeOffset occurredAt, CancellationToken ct)
    {
        if (!payload.TryGetProperty("callId", out var key) || !key.TryGetGuid(out var id)) return "suppressed";
        var call = await db.Calls.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id && row.ConversationId == conversationId, ct);
        if (call is null || (name == "CallIncoming" && (call.State != CallState.RINGING || call.ExpiresAt <= clock.GetUtcNow())))
            return "suppressed";
        var ids = await RecipientsAsync(call, null, name == "CallIncoming", ct);
        if (ids.Length == 0) return "offline";
        await hub.Clients.Clients(ids).SendAsync(name,
            new EventEnvelope<JsonElement>(eventId, 1, occurredAt, conversationId, payload), ct);
        return "submitted";
    }

    public async Task RelayAsync(Call call, CallActor actor, string name, object payload, CancellationToken ct)
    {
        var latest = await db.Calls.AsNoTracking().SingleAsync(row => row.Id == call.Id, ct);
        if (!latest.IsLive || latest.ExpiresAt <= clock.GetUtcNow() || latest.Revision != call.Revision) return;
        var ids = await RecipientsAsync(latest, actor, false, ct);
        if (ids.Length == 0) return;
        await hub.Clients.Clients(ids).SendAsync(name,
            new EventEnvelope<object>(Guid.NewGuid(), 1, clock.GetUtcNow(), call.ConversationId, payload), ct);
    }

    private async Task<string[]> RecipientsAsync(Call call, CallActor? sender, bool incoming, CancellationToken ct)
    {
        var ids = new List<string>();
        foreach (var connection in connections.Snapshot)
        {
            var actor = new CallActor(connection.UserId, connection.FamilyId, connection.Kind);
            if (sender is { } source && actor.Id == source.Id) continue;
            var selected = CallService.IsSelected(call, actor);
            var invited = call.State == CallState.RINGING &&
                (call.InitiatorIsClient ? actor.Kind == UserKind.BUSINESS_MEMBER : actor.Id == call.ClientUserId);
            if ((!selected && !invited) || (incoming && actor.Id == call.InitiatorUserId)) continue;
            if (await access.CanConnectAsync(connection, ct) && await access.CanReadAsync(call.ConversationId, connection, ct))
                ids.Add(connection.Id);
        }
        return ids.ToArray();
    }
}

