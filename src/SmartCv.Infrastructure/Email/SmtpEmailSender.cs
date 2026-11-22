using MailKit.Security;
using Microsoft.Extensions.Options;
using SmartCv.Core.Email;
using SmartCv.Core.Options;

namespace SmartCv.Infrastructure.Email;

public sealed class SmtpEmailSender(IOptions<EmailOptions> options, EmailMimeFactory factory) : IEmailSender
{
    public async Task SendAsync(long outboxId, EmailMessage message, CancellationToken cancellationToken = default)
    {
        var cfg = options.Value.Smtp;
        using var client = new MailKit.Net.Smtp.SmtpClient { Timeout = cfg.TimeoutSeconds * 1000 };
        // Bắt buộc nâng cấp lên TLS, không dùng Auto hoặc bỏ qua chứng chỉ.
        await client.ConnectAsync(cfg.Host, cfg.Port, SecureSocketOptions.StartTls, cancellationToken);
        if (!string.IsNullOrWhiteSpace(cfg.UserName))
        {
            if (string.IsNullOrWhiteSpace(cfg.Password))
                throw new InvalidOperationException("smtp_password_missing");
            await client.AuthenticateAsync(cfg.UserName, cfg.Password, cancellationToken);
        }
        await MailKit.IMailTransportExtensions.SendAsync(
            client,
            factory.Create(outboxId, message),
            cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
