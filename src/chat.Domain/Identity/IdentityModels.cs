namespace chat.Domain.Identity;

public enum UserKind { CLIENT, BUSINESS_MEMBER, PLATFORM_ADMIN }
public enum BusinessRole { OWNER, ADMIN, AGENT }

public sealed class User
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public UserKind Kind { get; init; }
    public required string Email { get; init; }
    public required string NormalizedEmail { get; init; }
    public required string DisplayName { get; init; }
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? EmailVerifiedAt { get; set; }
    public string? VerificationHash { get; set; }
    public DateTimeOffset? VerificationExpiresAt { get; set; }
    public string? ResetHash { get; set; }
    public DateTimeOffset? ResetExpiresAt { get; set; }
}

public sealed class RefreshSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid UserId { get; init; }
    public required Guid FamilyId { get; init; }
    public required string TokenHash { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class BusinessMembership
{
    public required Guid UserId { get; init; }
    public required Guid BusinessId { get; init; }
    public required BusinessRole Role { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
