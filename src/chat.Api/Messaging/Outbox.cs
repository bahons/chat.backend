using System.Text.Json;
using chat.Api.Middleware;
using chat.Domain.Messaging;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace chat.Api.Messaging;

internal static class Outbox
{
    public const string MessageCreated = "MessageCreated";
    public const string ConversationUpdated = "ConversationUpdated";
    public const string ConversationReadUpdated = "ConversationReadUpdated";
    public const string TypingChanged = "TypingChanged";
    public const string BusinessAccessRevoked = "BusinessAccessRevoked";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task RequireCapacityAsync(ChatDbContext db, int needed, CancellationToken ct)
    {
        const int limit = 10000;
        var active = await db.OutboxMessages.Where(row => row.CompletedAt == null).Take(limit).CountAsync(ct);
        if (active > limit - needed)
        {
            throw new ApiFailure(StatusCodes.Status503ServiceUnavailable);
        }
    }

    public static void Add(ChatDbContext db, string name, DateTimeOffset now, object payload,
        Guid? conversationId = null, Guid? businessId = null) =>
        db.OutboxMessages.Add(new OutboxMessage
        {
            EventName = name,
            ConversationId = conversationId,
            BusinessId = businessId,
            OccurredAt = now,
            AvailableAt = now,
            Payload = JsonSerializer.Serialize(payload, Json)
        });
}
