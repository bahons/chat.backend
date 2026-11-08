namespace chat.Application.Push;

public enum PushResultKind { Accepted, Delivered, Retry, Unregistered, Rejected }
public sealed record PushResult(PushResultKind Kind, string? TicketId = null);

public interface IPushGateway
{
    Task<PushResult> SendAsync(string token, Guid eventId, Guid conversationId, CancellationToken ct);
    Task<PushResult> CheckAsync(string ticketId, CancellationToken ct);
}
