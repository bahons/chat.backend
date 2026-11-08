using chat.Domain.Messaging;

namespace chat.Domain.Identity;

public sealed class AccountMail : ILeasedWork
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid UserId { get; init; }
    public required bool IsPasswordReset { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required DateTimeOffset AvailableAt { get; set; }
    public byte[]? Payload { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int Attempts { get; set; }
    public Guid? LeaseId { get; set; }
    public string? Outcome { get; set; }
}
