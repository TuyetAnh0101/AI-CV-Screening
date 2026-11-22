using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartCv.Core.Email;
using SmartCv.Core.Options;

namespace SmartCv.Infrastructure.Email;

public sealed class EmailDispatchProcessor(
    IEmailOutboxStore store, IEmailSender sender, IDataProtectionProvider provider,
    TimeProvider time, IOptions<EmailOptions> options, ILogger<EmailDispatchProcessor> logger)
{
    private readonly IDataProtector protector = provider.CreateProtector(EmailQueue.Purpose);
    // 2 phút lớn hơn timeout SMTP <=90s. Nếu bị treo >lease, vẫn tồn tại rủi ro gửi lặp (at-least-once).
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var count = 0;
        // Claim từng dòng ngay trước khi gửi. Không claim 20 dòng rồi gửi tuần tự:
        // dòng cuối có thể hết hạn lease trong lúc 19 SMTP call trước vẫn đang chạy.
        for (var n = 0; n < options.Value.BatchSize; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = time.GetUtcNow().UtcDateTime;
            var claim = Guid.NewGuid();
            var ids = await store.ClaimDueAsync(claim, 1, now, now.AddMinutes(2), cancellationToken);
            if (ids.Count == 0) break;
            var id = ids[0];
            var row = await store.ReadClaimedAsync(id, claim, cancellationToken);
            if (row is null) continue;
            try
            {
                var plaintext = protector.Unprotect(row.ProtectedPayload ?? throw new CryptographicException());
                var message = JsonSerializer.Deserialize<EmailMessage>(plaintext) ?? throw new CryptographicException();
                await sender.SendAsync(id, message, cancellationToken);
                if (await store.MarkSentAsync(id, claim, time.GetUtcNow().UtcDateTime, cancellationToken)) count++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // Không bao giờ log Exception/Message, ToAddress, payload hoặc URL có token.
                var code = FailureCode(error);
                var attempts = (byte)Math.Min(3, row.Attempts + 1);
                var terminal = attempts >= 3 || error is CryptographicException or JsonException;
                var delay = attempts switch { 1 => 1, 2 => 5, _ => 15 };
                var next = time.GetUtcNow().UtcDateTime.AddMinutes(delay);
                await store.MarkFailureAsync(id, claim, attempts, next, terminal, code, cancellationToken);
                logger.LogWarning("Email outbox {EmailOutboxId} attempt {Attempt} failed code={Code} terminal={Terminal}",
                    id, attempts, code, terminal);
            }
        }
        return count;
    }

    private static string FailureCode(Exception error) => error switch
    {
        CryptographicException or JsonException => "payload_unprotect_failed",
        System.Security.Authentication.AuthenticationException => "smtp_auth_failed",
        MailKit.Net.Smtp.SmtpCommandException => "smtp_command_failed",
        MailKit.Net.Smtp.SmtpProtocolException => "smtp_protocol_failed",
        IOException or UnauthorizedAccessException => "io_failed",
        TimeoutException => "timeout",
        _ => "email_send_failed"
    };
}
