namespace chat.Domain.Businesses;

public enum BusinessStatus { PENDING_VERIFICATION, VERIFIED, REJECTED, SUSPENDED }

public sealed class Business
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string LegalName { get; init; }
    public required string DisplayName { get; set; }
    public required string RegistrationNumber { get; init; }
    public required string Country { get; init; }
    public required string Category { get; set; }
    public string? Description { get; set; }
    public BusinessStatus Status { get; set; } = BusinessStatus.PENDING_VERIFICATION;
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class BusinessApplication
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid BusinessId { get; init; }
    public required Guid OwnerUserId { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public string? Decision { get; set; }
    public string? Reason { get; set; }
}

public sealed class ApplicationDraft
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string TokenHash { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public enum VerificationScanStatus { PENDING, READY, QUARANTINED }

public sealed class VerificationDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid DraftId { get; init; }
    public Guid? ApplicationId { get; set; }
    public required string ContentType { get; init; }
    public required int Size { get; init; }
    public required byte[] Content { get; init; }
    public VerificationScanStatus ScanStatus { get; set; } = VerificationScanStatus.PENDING;
    public DateTimeOffset? ScanAfter { get; set; }
}

public sealed class AuditLog
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ActorId { get; init; }
    public required string Action { get; init; }
    public required Guid EntityId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? Reason { get; init; }
}
