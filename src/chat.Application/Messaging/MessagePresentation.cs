using chat.Contracts.Messaging;
using chat.Domain.Messaging;

namespace chat.Application.Messaging;

public static class MessagePresentation
{
    public static string? Preview(string? text) => text is { Length: > 160 }
        ? text[..(char.IsHighSurrogate(text[159]) ? 159 : 160)]
        : text;

    public static MessageView View(Message message, IEnumerable<Attachment> attachments) =>
        new(message.Id, message.ConversationId, message.ClientMessageId, message.AuthorSide.ToString(),
            message.Type.ToString(), message.Text,
            attachments.Select(row => new MessageAttachmentView(row.Id, row.ContentType, row.Size)).ToArray(),
            message.CreatedAt);
}
