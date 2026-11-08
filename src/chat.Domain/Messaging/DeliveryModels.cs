namespace chat.Domain.Messaging;

public enum AttachmentStatus { PENDING, READY, QUARANTINED }

public sealed class Attachment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ConversationId { get; init; }
    public required Guid UploaderUserId { get; init; }
    public required string ContentType { get; init; }
    public required int Size { get; init; }
    public required DateTimeOffset UploadExpiresAt { get; init; }
    public AttachmentStatus Status { get; set; }
    public Guid? MessageId { get; set; }
    public string? Checksum { get; set; }
    public Guid? UploadFamilyId { get; init; }
    public DateTimeOffset? RetiredAt { get; set; }
    public DateTimeOffset? CleanupAfter { get; set; }
    public DateTimeOffset? ObjectsDeletedAt { get; set; }
    public int CleanupAttempts { get; set; }
}

public sealed class DeviceRegistration
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid UserId { get; set; }
    public required Guid FamilyId { get; set; }
    public required string PushToken { get; init; }
    public required DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
}

public sealed class PushDelivery : ILeasedWork
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid EventId { get; init; }
    public required Guid DeviceId { get; init; }
    public required Guid UserId { get; init; }
    public required Guid FamilyId { get; init; }
    public required Guid ConversationId { get; init; }
    public required DateTimeOffset AvailableAt { get; set; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? TicketId { get; set; }
    public int Attempts { get; set; }
    public Guid? LeaseId { get; set; }
    public string? Outcome { get; set; }
}

public interface ILeasedWork
{
    Guid Id { get; }
    DateTimeOffset AvailableAt { get; set; }
    DateTimeOffset? CompletedAt { get; set; }
    int Attempts { get; set; }
    Guid? LeaseId { get; set; }
    string? Outcome { get; set; }
}
