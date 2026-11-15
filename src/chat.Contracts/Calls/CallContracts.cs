using System.ComponentModel.DataAnnotations;

namespace chat.Contracts.Calls;

public sealed record CreateCallRequest(Guid ConversationId,
    [property: Required, RegularExpression("^(AUDIO|VIDEO)$")] string Kind,
    Guid? ClientCallId = null);

public sealed record CallView(Guid Id, Guid ConversationId, string Kind, string State,
    bool InitiatorIsClient, bool CanAccept, bool CanSignal, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt,
    string? EndedReason, int Revision);

public sealed record IceServer(string[] Urls, string? Username, string? Credential);
public sealed record IceConfiguration(IceServer[] IceServers, DateTimeOffset ExpiresAt);

public sealed record IceCandidate(
    [property: Required, StringLength(2048)] string Candidate,
    [property: StringLength(64)] string? SdpMid,
    [property: Range(0, 32)] int? SdpMLineIndex,
    [property: StringLength(256)] string? UsernameFragment = null);

public sealed record CallStateEvent(Guid CallId, string State, string? EndedReason, int Revision, DateTimeOffset ExpiresAt);
public sealed record CallIncomingEvent(Guid CallId, Guid ConversationId, string Kind, DateTimeOffset ExpiresAt);
public sealed record CallSdpEvent(Guid CallId, string Sdp, int Revision);
public sealed record CallIceEvent(Guid CallId, IceCandidate Candidate, int Revision);

