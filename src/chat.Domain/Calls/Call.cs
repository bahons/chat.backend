namespace chat.Domain.Calls;

public enum CallKind { AUDIO, VIDEO }
public enum CallState { RINGING, CONNECTING, ACTIVE, ENDED, MISSED, REJECTED }

public sealed class Call
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ConversationId { get; init; }
    public required Guid InitiatorUserId { get; init; }
    public required Guid InitiatorFamilyId { get; init; }
    public required Guid ClientCallId { get; init; }
    public required Guid ClientUserId { get; init; }
    public required Guid BusinessId { get; init; }
    public required bool InitiatorIsClient { get; init; }
    public Guid? OperatorUserId { get; set; }
    public Guid? OperatorFamilyId { get; set; }
    public Guid? ClientFamilyId { get; set; }
    public required CallKind Kind { get; init; }
    public CallState State { get; private set; } = CallState.RINGING;
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public string? EndedReason { get; private set; }
    public int Revision { get; private set; }
    public bool AwaitingAnswer { get; private set; }

    public bool IsLive => State is CallState.RINGING or CallState.CONNECTING or CallState.ACTIVE;

    public bool Accept(DateTimeOffset now)
    {
        if (!IsLive || Expire(now) || State != CallState.RINGING) return false;
        State = CallState.CONNECTING;
        ExpiresAt = Deadline(now.AddSeconds(60));
        Revision++;
        return true;
    }

    public bool Offer(DateTimeOffset now)
    {
        if (!IsLive || Expire(now) || State is not (CallState.CONNECTING or CallState.ACTIVE)) return false;
        State = CallState.CONNECTING;
        ExpiresAt = Deadline(now.AddSeconds(60));
        AwaitingAnswer = true;
        Revision++;
        return true;
    }

    public bool Answer(DateTimeOffset now)
    {
        if (!IsLive || Expire(now) || State != CallState.CONNECTING || !AwaitingAnswer) return false;
        State = CallState.ACTIVE;
        StartedAt ??= now;
        ExpiresAt = Deadline(now.AddHours(2));
        AwaitingAnswer = false;
        Revision++;
        return true;
    }

    public bool Finish(DateTimeOffset now, CallState state, string reason)
    {
        if (!IsLive) return false;
        if (state is not (CallState.ENDED or CallState.MISSED or CallState.REJECTED))
            throw new ArgumentOutOfRangeException(nameof(state));
        State = state;
        EndedAt = now;
        EndedReason = reason;
        AwaitingAnswer = false;
        Revision++;
        return true;
    }

    public bool Expire(DateTimeOffset now) => IsLive && ExpiresAt <= now &&
        Finish(now, State == CallState.RINGING ? CallState.MISSED : CallState.ENDED, "TIMEOUT");

    private DateTimeOffset Deadline(DateTimeOffset proposed) =>
        proposed < CreatedAt.AddHours(2) ? proposed : CreatedAt.AddHours(2);
}

