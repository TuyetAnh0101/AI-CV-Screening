using Microsoft.Extensions.Logging;
using SmartCv.Core.Email;

namespace SmartCv.Infrastructure.Email;

public sealed class DisabledEmailSender(ILogger<DisabledEmailSender> logger) : IEmailSender
{
    public Task SendAsync(long outboxId, EmailMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("Email disabled. OutboxId={OutboxId}. No actual delivery performed.", outboxId);
        return Task.CompletedTask;
    }
}
