using chat.Api.Identity;
using chat.Api.Messaging;
using chat.Api.Middleware;
using chat.Contracts.Calls;
using chat.Domain.Calls;
using chat.Domain.Identity;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace chat.Api.Calls;

internal readonly record struct CallActor(Guid Id, Guid Family, UserKind Kind);

internal sealed class CallService(ChatDbContext db, ConversationAccess access, TimeProvider clock,
    TurnCredentials turn, IOptions<IdentityOptions> identity)
{
    public async Task<CallView> CreateAsync(CallActor actor, CreateCallRequest request, CancellationToken ct)
    {
        turn.RequireConfigured();
        if (request.ConversationId == Guid.Empty || request.ClientCallId == Guid.Empty) throw new ApiFailure(400);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var conversation = await access.LockAsync(request.ConversationId, actor.Id, actor.Kind, ct);
        await RequireActorAsync(actor, ct, exclusive: true);
        var previous = request.ClientCallId is { } key
            ? await db.Calls.SingleOrDefaultAsync(call => call.InitiatorUserId == actor.Id && call.ClientCallId == key, ct)
            : null;
        if (previous is not null)
        {
            if (previous.ConversationId != request.ConversationId || previous.Kind.ToString() != request.Kind) throw new ApiFailure(409);
            await ExpireAsync(previous, ct);
            await transaction.CommitAsync(ct);
            return View(previous, actor);
        }

        var live = await db.Calls.SingleOrDefaultAsync(call => call.ConversationId == conversation.Id
            && (call.State == CallState.RINGING || call.State == CallState.CONNECTING || call.State == CallState.ACTIVE), ct);
        if (live is not null)
        {
            await ExpireAsync(live, ct);
            if (live.IsLive) throw new ApiFailure(409);
        }
        if (await db.Calls.CountAsync(call => call.InitiatorUserId == actor.Id && call.CreatedAt > clock.GetUtcNow().AddMinutes(-1), ct) >= 5)
            throw new ApiFailure(429);
        await Outbox.RequireCapacityAsync(db, 2, ct);
        var now = clock.GetUtcNow();
        var call = new Call
        {
            ConversationId = conversation.Id,
            ClientUserId = conversation.ClientUserId,
            BusinessId = conversation.BusinessId,
            InitiatorUserId = actor.Id,
            InitiatorFamilyId = actor.Family,
            InitiatorIsClient = actor.Kind == UserKind.CLIENT,
            OperatorUserId = actor.Kind == UserKind.BUSINESS_MEMBER ? actor.Id : null,
            OperatorFamilyId = actor.Kind == UserKind.BUSINESS_MEMBER ? actor.Family : null,
            ClientFamilyId = actor.Kind == UserKind.CLIENT ? actor.Family : null,
            ClientCallId = request.ClientCallId ?? Guid.NewGuid(),
            Kind = Enum.Parse<CallKind>(request.Kind),
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(45)
        };
        db.Calls.Add(call);
        Outbox.Add(db, "CallIncoming", now, new CallIncomingEvent(call.Id, call.ConversationId, call.Kind.ToString(), call.ExpiresAt), call.ConversationId);
        AddState(call);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return View(call, actor);
    }

    public async Task<CallView> ReadAsync(Guid id, CallActor actor, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var call = await LockAsync(id, actor, ct);
        await ExpireAsync(call, ct);
        await transaction.CommitAsync(ct);
        return View(call, actor);
    }

    public async Task<CallView> ChangeAsync(Guid id, CallActor actor, string action, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var call = await LockAsync(id, actor, ct);
        await ExpireAsync(call, ct);
        var changed = false;
        if (action == "accept")
        {
            if (!IsSelected(call, actor) && call.State != CallState.RINGING) throw new ApiFailure(409);
            if (call.State == CallState.RINGING && IsCallee(call, actor))
            {
                if (actor.Kind == UserKind.BUSINESS_MEMBER) { call.OperatorUserId = actor.Id; call.OperatorFamilyId = actor.Family; }
                else call.ClientFamilyId = actor.Family;
                changed = call.Accept(clock.GetUtcNow());
            }
            else if (!IsSelected(call, actor) || actor.Id == call.InitiatorUserId || !call.IsLive) throw new ApiFailure(409);
        }
        else if (action == "reject")
        {
            if (!IsCallee(call, actor) || (call.State != CallState.RINGING && call.State != CallState.REJECTED)) throw new ApiFailure(409);
            if (call.State == CallState.RINGING)
            {
                if (actor.Kind == UserKind.BUSINESS_MEMBER) { call.OperatorUserId = actor.Id; call.OperatorFamilyId = actor.Family; }
                else call.ClientFamilyId = actor.Family;
            }
            changed = call.Finish(clock.GetUtcNow(), CallState.REJECTED, "DECLINED");
        }
        else
        {
            if (!IsSelected(call, actor)) throw new ApiFailure(404);
            changed = call.Finish(clock.GetUtcNow(), CallState.ENDED, "HANGUP");
        }
        if (changed)
        {
            await Outbox.RequireCapacityAsync(db, 1, ct);
            AddState(call);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return View(call, actor);
    }

    public async Task<IceConfiguration> IceAsync(Guid id, CallActor actor, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var call = await LockAsync(id, actor, ct);
        await ExpireAsync(call, ct);
        if (!IsSelected(call, actor)) throw new ApiFailure(404);
        if (call.State is not (CallState.CONNECTING or CallState.ACTIVE)) throw new ApiFailure(409);
        var result = turn.Issue(call);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<Call> SignalAsync(Guid id, CallActor actor, string method, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var call = await LockAsync(id, actor, ct);
        await ExpireAsync(call, ct);
        if (!IsSelected(call, actor)) throw new ApiFailure(404);
        if (call.State is not (CallState.CONNECTING or CallState.ACTIVE)) throw new ApiFailure(409);
        var changed = method switch
        {
            "SendOffer" when actor.Id == call.InitiatorUserId => call.Offer(clock.GetUtcNow()),
            "SendAnswer" when actor.Id != call.InitiatorUserId => call.Answer(clock.GetUtcNow()),
            "SendIceCandidate" => false,
            _ => throw new ApiFailure(409)
        };
        if (method != "SendIceCandidate" && !changed) throw new ApiFailure(409);
        if (changed)
        {
            await Outbox.RequireCapacityAsync(db, 1, ct);
            AddState(call);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return call;
    }

    internal async Task<Call> LockAsync(Guid id, CallActor actor, CancellationToken ct)
    {
        var visible = await db.Calls.AsNoTracking().SingleOrDefaultAsync(call => call.Id == id, ct) ?? throw new ApiFailure(404);
        await access.LockAsync(visible.ConversationId, actor.Id, actor.Kind, ct);
        await RequireActorAsync(actor, ct);
        var rows = await db.Calls.FromSql($"SELECT * FROM \"Calls\" WHERE \"Id\" = {id} FOR UPDATE").ToListAsync(ct);
        var call = rows.Single();
        // Before selection, eligible employees may inspect the invitation. Afterwards, only the selected peers.
        if (call.State != CallState.RINGING && !IsSelected(call, actor) && actor.Id != call.InitiatorUserId)
            throw new ApiFailure(404);
        var callerKind = call.InitiatorIsClient ? UserKind.CLIENT : UserKind.BUSINESS_MEMBER;
        var valid = await EligibleAsync(call.InitiatorUserId, call.InitiatorFamilyId, callerKind, call.ConversationId, ct);
        if (valid && call.State is CallState.CONNECTING or CallState.ACTIVE)
            valid = await EligibleAsync(call.ClientUserId, call.ClientFamilyId!.Value, UserKind.CLIENT, call.ConversationId, ct)
                && await EligibleAsync(call.OperatorUserId!.Value, call.OperatorFamilyId!.Value, UserKind.BUSINESS_MEMBER, call.ConversationId, ct);
        if (!valid && call.Finish(clock.GetUtcNow(), CallState.ENDED, "ACCESS_REVOKED"))
        {
            AddState(call);
            await db.SaveChangesAsync(ct);
        }
        return call;
    }

    private async Task<bool> EligibleAsync(Guid actor, Guid family, UserKind kind, Guid conversationId, CancellationToken ct) =>
        await db.Users.AnyAsync(user => user.Id == actor && user.Kind == kind && user.IsActive
            && (!identity.Value.RequireConfirmedEmail || user.EmailVerifiedAt != null), ct)
        && await AuthPrincipal.ActiveSessions(db, clock.GetUtcNow()).AnyAsync(session => session.UserId == actor && session.FamilyId == family, ct)
        && await access.VisibleTo(actor, kind).AnyAsync(room => room.Id == conversationId, ct);

    private async Task RequireActorAsync(CallActor actor, CancellationToken ct, bool exclusive = false)
    {
        var users = exclusive
            ? await db.Users.FromSql($"SELECT * FROM \"Users\" WHERE \"Id\" = {actor.Id} FOR UPDATE").AsNoTracking().ToListAsync(ct)
            : await db.Users.FromSql($"SELECT * FROM \"Users\" WHERE \"Id\" = {actor.Id} FOR SHARE").AsNoTracking().ToListAsync(ct);
        if (users.SingleOrDefault() is not { IsActive: true } user || user.Kind != actor.Kind
            || (identity.Value.RequireConfirmedEmail && user.EmailVerifiedAt is null)
            || !await AuthPrincipal.ActiveSessions(db, clock.GetUtcNow()).AnyAsync(session => session.UserId == actor.Id && session.FamilyId == actor.Family, ct))
            throw new ApiFailure(401);
    }

    private async Task ExpireAsync(Call call, CancellationToken ct)
    {
        if (call.Expire(clock.GetUtcNow()))
        {
            AddState(call);
            await db.SaveChangesAsync(ct);
        }
    }

    internal void AddState(Call call) => Outbox.Add(db, "CallStateChanged", clock.GetUtcNow(),
        new CallStateEvent(call.Id, call.State.ToString(), call.EndedReason, call.Revision, call.ExpiresAt), call.ConversationId);

    internal static bool IsSelected(Call call, CallActor actor) =>
        actor.Kind == UserKind.CLIENT
            ? call.ClientUserId == actor.Id && call.ClientFamilyId == actor.Family
            : call.OperatorUserId == actor.Id && call.OperatorFamilyId == actor.Family;

    private static bool IsCallee(Call call, CallActor actor) =>
        call.InitiatorIsClient ? actor.Kind == UserKind.BUSINESS_MEMBER : actor.Kind == UserKind.CLIENT;

    private static CallView View(Call call, CallActor actor) => new(call.Id, call.ConversationId, call.Kind.ToString(), call.State.ToString(),
        call.InitiatorIsClient, call.State == CallState.RINGING && IsCallee(call, actor),
        call.State is CallState.CONNECTING or CallState.ACTIVE && IsSelected(call, actor),
        call.CreatedAt, call.ExpiresAt, call.StartedAt, call.EndedAt, call.EndedReason, call.Revision);
}

