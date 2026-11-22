using Microsoft.Extensions.Options;
using MimeKit;
using SmartCv.Core.Email;
using SmartCv.Core.Options;

namespace SmartCv.Infrastructure.Email;

public sealed class EmailMimeFactory(IOptions<EmailOptions> options)
{
    public MimeMessage Create(long outboxId, EmailMessage mail)
    {
        if (outboxId <= 0) throw new ArgumentOutOfRangeException(nameof(outboxId));
        var opts = options.Value;
        var message = new MimeMessage();
        var from = MailboxAddress.Parse(opts.FromAddress);
        message.From.Add(new MailboxAddress(opts.FromName, from.Address));
        message.To.Add(MailboxAddress.Parse(mail.ToAddress));
        message.Subject = mail.Subject;
        message.MessageId = $"scapp-{outboxId}@{from.Address.Split('@').Last()}";
        message.Body = new BodyBuilder { HtmlBody = mail.Html, TextBody = mail.Text }.ToMessageBody();
        return message;
    }
}
