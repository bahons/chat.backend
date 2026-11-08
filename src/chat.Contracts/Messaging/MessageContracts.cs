using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace chat.Contracts.Messaging;

public sealed record CreateConversationRequest(Guid BusinessId);
public sealed record SendMessageRequest(Guid ClientMessageId,
    [property: Required, RegularExpression("^(TEXT|IMAGE|FILE)$")] string Type,
    [property: StringLength(4000)] string? Text,
    [property: MaxLength(5)] Guid[]? AttachmentIds);
public sealed record ReadConversationRequest(Guid LastMessageId);

public sealed record ConversationView(Guid Id, Guid BusinessId, string BusinessName, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, Guid? LastMessageId, string? LastMessagePreview, string Side, ConversationReadState ReadState);
public sealed record ConversationReadState(ReadPosition? Client, ReadPosition? Business);
public sealed record ReadPosition(Guid LastMessageId, DateTimeOffset UpdatedAt);

public sealed record MessageAttachmentView(Guid Id, string ContentType, long Size);
public sealed record MessageView(Guid Id, Guid ConversationId, Guid ClientMessageId, string AuthorSide,
    string Type, string? Text, IReadOnlyList<MessageAttachmentView> Attachments, DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt = null);
public sealed record ReadReceiptView(Guid ConversationId, string ReaderSide, Guid LastMessageId, DateTimeOffset UpdatedAt);

public sealed record EventEnvelope<T>(Guid EventId, int EventVersion, DateTimeOffset OccurredAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ConversationId, T Payload);
