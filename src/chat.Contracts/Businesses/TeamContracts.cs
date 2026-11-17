using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace chat.Contracts.Businesses;

public sealed record InviteTeamRequest([property: Required, EmailAddress, MaxLength(254)] string Email,
    [property: Required, RegularExpression("^(OWNER|ADMIN|AGENT)$")] string Role);
public sealed record AcceptInvitationRequest([property: Required, StringLength(43, MinimumLength = 43)] string Token,
    [property: StringLength(128, MinimumLength = 12)] string? Password = null,
    [property: StringLength(100, MinimumLength = 1)] string? DisplayName = null);
public sealed record UpdateMemberRequest([property: Required, RegularExpression("^(OWNER|ADMIN|AGENT)$")] string Role,
    [property: JsonRequired] bool IsActive);
public sealed record AssignmentRequest([property: JsonRequired] Guid? UserId,
    [property: JsonRequired, Range(0, long.MaxValue)] long ExpectedRevision);
public sealed record TeamMemberView(Guid UserId, string DisplayName, string Role, bool IsActive);
public sealed record InvitationView(Guid Id, Guid BusinessId, string Email, string Role, DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt, DateTimeOffset? RevokedAt);
public sealed record InvitationCreated(InvitationView Invitation, string Token);
public sealed record AssignmentView(Guid ConversationId, Guid? UserId, long Revision, DateTimeOffset? UpdatedAt);
public sealed record BusinessAnalytics(DateTimeOffset From, DateTimeOffset To, long Conversations,
    long ClientMessages, long BusinessMessages, long RespondedConversations, long UnansweredConversations,
    double? AverageFirstResponseMilliseconds);
