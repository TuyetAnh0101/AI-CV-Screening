namespace SmartCv.Core.Email;

public interface IEmailSender
{
    Task SendAsync(long outboxId, EmailMessage message, CancellationToken cancellationToken = default);
}
