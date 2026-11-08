namespace chat.Application.Identity;

public interface IAccountEmailSender
{
    Task SendAsync(AccountEmail message, CancellationToken cancellationToken);
}

public sealed record AccountEmail(string Recipient, string Subject, string Body, Guid? IntentId = null);
