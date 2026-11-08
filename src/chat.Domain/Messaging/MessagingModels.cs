namespace chat.Domain.Messaging;

public enum ParticipantSide { CLIENT, BUSINESS }
public enum MessageType { TEXT, IMAGE, FILE }

public sealed class Conversation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ClientUserId { get; init; }
    public required Guid BusinessId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; set; }
    public Guid? LastMessageId { get; set; }
    public string? LastMessagePreview { get; set; }
}

public sealed class Message
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ConversationId { get; init; }
    public required Guid AuthorUserId { get; init; }
    public required ParticipantSide AuthorSide { get; init; }
    public required Guid ClientMessageId { get; init; }
    public required MessageType Type { get; init; }
    public string? Text { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class MessageReadReceipt
{
    public required Guid ConversationId { get; init; }
    public required ParticipantSide Side { get; init; }
    public required Guid LastMessageId { get; set; }
    public required DateTimeOffset LastMessageAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}

public sealed class OutboxMessage : ILeasedWork
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid? ConversationId { get; init; }
    public Guid? BusinessId { get; init; }
    public required string EventName { get; init; }
    public required string Payload { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int Attempts { get; set; }
    public Guid? LeaseId { get; set; }
    public string? Outcome { get; set; }
}
