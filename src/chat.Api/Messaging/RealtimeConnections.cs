using System.Collections.Concurrent;
using chat.Api.Identity;
using chat.Domain.Businesses;
using chat.Domain.Identity;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace chat.Api.Messaging;

internal sealed class LiveConnection(string id, Guid userId, Guid familyId, UserKind kind, Action abort)
{
    private long lastTyping;
    public string Id { get; } = id;
    public Guid UserId { get; } = userId;
    public Guid FamilyId { get; } = familyId;
    public UserKind Kind { get; } = kind;
    public Action Abort { get; } = abort;
    public Guid[] BusinessIds { get; set; } = [];
    public ConcurrentDictionary<Guid, byte> Conversations { get; } = new();

    public bool AllowTyping(DateTimeOffset now)
    {
        var previous = Interlocked.Read(ref lastTyping);
        return now.UtcTicks - previous >= TimeSpan.TicksPerSecond
            && Interlocked.CompareExchange(ref lastTyping, now.UtcTicks, previous) == previous;
    }
}

internal sealed class RealtimeConnections
{
    public const int SubscriptionLimit = 16;
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, LiveConnection> connections = new(StringComparer.Ordinal);

    public LiveConnection[] Snapshot => connections.Values.ToArray();
    public LiveConnection? Find(string id) => connections.GetValueOrDefault(id);

    public bool TryAdd(LiveConnection connection)
    {
        lock (gate)
        {
            return connections.Count < 256 && connections.Values.Count(item => item.UserId == connection.UserId) < 4
                && connections.TryAdd(connection.Id, connection);
        }
    }

    public void Remove(string id) => connections.TryRemove(id, out _);
}

internal sealed class RealtimeAccess(ChatDbContext db, ConversationAccess access, TimeProvider clock)
{
    private const int BusinessLimit = 32;

    public async Task<bool> InitializeAsync(LiveConnection connection, CancellationToken ct)
    {
        var snapshot = await SnapshotAsync([connection], [], ct);
        if (!snapshot.HasSession(connection))
        {
            return false;
        }

        if (connection.Kind == UserKind.CLIENT)
        {
            return true;
        }

        var businesses = snapshot.Businesses[connection.UserId].Select(row => row.BusinessId)
            .Take(BusinessLimit + 1).ToArray();
        if (connection.Kind != UserKind.BUSINESS_MEMBER || businesses.Length is 0 or > BusinessLimit)
        {
            return false;
        }

        connection.BusinessIds = businesses;
        return true;
    }

    public async Task<bool> CanConnectAsync(LiveConnection connection, CancellationToken ct) =>
        (await SnapshotAsync([connection], [], ct)).CanConnect(connection);

    public Task<bool> CanReadAsync(Guid id, LiveConnection connection, CancellationToken ct) =>
        access.VisibleTo(connection.UserId, connection.Kind).AnyAsync(row => row.Id == id, ct);

    public async Task<string[]> RecipientsAsync(Guid id, LiveConnection[] connections, CancellationToken ct)
    {
        var candidates = connections.Where(connection => connection.Conversations.ContainsKey(id)).ToArray();
        if (candidates.Length == 0)
        {
            return [];
        }

        var snapshot = await SnapshotAsync(candidates, [id], ct);
        var recipients = new List<string>();
        foreach (var connection in candidates)
        {
            if (snapshot.CanConnect(connection) && snapshot.CanRead(id, connection))
            {
                recipients.Add(connection.Id);
            }
            else
            {
                connection.Abort();
            }
        }

        return recipients.ToArray();
    }

    public async Task RecheckAsync(LiveConnection[] connections, CancellationToken ct)
    {
        if (connections.Length == 0)
        {
            return;
        }

        var subscriptions = connections.Select(connection => (Connection: connection, Ids: connection.Conversations.Keys.ToArray())).ToArray();
        var snapshot = await SnapshotAsync(connections, subscriptions.SelectMany(item => item.Ids).Distinct().ToArray(), ct);
        foreach (var (connection, ids) in subscriptions)
        {
            if (!snapshot.CanConnect(connection) || ids.Any(id => !snapshot.CanRead(id, connection)))
            {
                connection.Abort();
            }
        }
    }

    private async Task<AccessSnapshot> SnapshotAsync(LiveConnection[] connections, Guid[] ids, CancellationToken ct)
    {
        var users = connections.Select(connection => connection.UserId).Distinct().ToArray();
        var families = connections.Select(connection => connection.FamilyId).Distinct().ToArray();
        var sessions = await (from session in AuthPrincipal.ActiveSessions(db, clock.GetUtcNow())
                              join user in db.Users on session.UserId equals user.Id
                              where users.Contains(user.Id) && families.Contains(session.FamilyId)
                                  && user.IsActive && user.EmailVerifiedAt != null
                              select new Session(user.Id, session.FamilyId, user.Kind)).Distinct().ToListAsync(ct);
        var eligible = ConversationAccess.EligibleMemberships(db).Where(membership => db.Businesses.Any(business =>
            business.Id == membership.BusinessId && business.IsActive && business.Status == BusinessStatus.VERIFIED));
        var businesses = await db.Users.Where(user => users.Contains(user.Id) && user.Kind == UserKind.BUSINESS_MEMBER)
            .SelectMany(user => eligible.Where(membership => membership.UserId == user.Id)
                .OrderBy(membership => membership.BusinessId).Take(BusinessLimit + 1))
            .Select(membership => new Entitlement(membership.UserId, membership.BusinessId)).ToListAsync(ct);
        var rooms = ids.Length == 0 ? new Dictionary<Guid, Room>() : await (from conversation in db.Conversations
                                                                            join business in db.Businesses on conversation.BusinessId equals business.Id
                                                                            where ids.Contains(conversation.Id) && business.IsActive && business.Status == BusinessStatus.VERIFIED
                                                                            select new Room(conversation.Id, conversation.ClientUserId, business.Id)).ToDictionaryAsync(room => room.Id, ct);
        return new AccessSnapshot(sessions.ToHashSet(), businesses.ToLookup(row => row.UserId), rooms);
    }

    private sealed record Session(Guid UserId, Guid FamilyId, UserKind Kind);
    private sealed record Entitlement(Guid UserId, Guid BusinessId);
    private sealed record Room(Guid Id, Guid ClientUserId, Guid BusinessId);

    private sealed record AccessSnapshot(HashSet<Session> Sessions, ILookup<Guid, Entitlement> Businesses, Dictionary<Guid, Room> Rooms)
    {
        public bool HasSession(LiveConnection connection) =>
            Sessions.Contains(new Session(connection.UserId, connection.FamilyId, connection.Kind));

        public bool CanConnect(LiveConnection connection) => HasSession(connection)
            && (connection.Kind == UserKind.CLIENT || (connection.Kind == UserKind.BUSINESS_MEMBER
                && connection.BusinessIds.Length > 0 && Businesses[connection.UserId].Count() <= BusinessLimit
                && connection.BusinessIds.All(id => HasBusiness(connection, id))));

        public bool CanRead(Guid id, LiveConnection connection) => Rooms.TryGetValue(id, out var room)
            && (connection.Kind == UserKind.CLIENT ? room.ClientUserId == connection.UserId : HasBusiness(connection, room.BusinessId));

        private bool HasBusiness(LiveConnection connection, Guid id) =>
            Businesses[connection.UserId].Contains(new Entitlement(connection.UserId, id));
    }
}
