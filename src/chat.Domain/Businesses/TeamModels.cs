using chat.Domain.Identity;

namespace chat.Domain.Businesses;

public sealed class TeamInvitation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid BusinessId { get; init; }
    public required Guid CreatedBy { get; init; }
    public required string Email { get; init; }
    public required string TokenHash { get; init; }
    public required BusinessRole Role { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class ConversationAssignment
{
    public required Guid ConversationId { get; init; }
    public required Guid BusinessId { get; init; }
    public Guid? UserId { get; set; }
    public required Guid UpdatedBy { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}
