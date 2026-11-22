using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SmartCv.Core.Email;
using SmartCv.Core.Options;

namespace SmartCv.Infrastructure.Email;

public sealed class FileEmailSender(IOptions<EmailOptions> options, IHostEnvironment host, EmailMimeFactory factory) : IEmailSender
{
    public async Task SendAsync(long outboxId, EmailMessage message, CancellationToken cancellationToken = default)
    {
        var configured = options.Value.PickupDirectory;
        var folder = Path.GetFullPath(Path.IsPathFullyQualified(configured)
            ? configured : Path.Combine(host.ContentRootPath, configured));
        Directory.CreateDirectory(folder);
        var final = Path.Combine(folder, $"scapp-email-{outboxId:D12}.eml");
        if (File.Exists(final)) return; // idempotent trên file pickup với một ID duy nhất
        var temp = Path.Combine(folder, $".tmp-{Guid.NewGuid():N}");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 8192, FileOptions.Asynchronous))
            {
                await factory.Create(outboxId, message).WriteToAsync(stream, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            try { File.Move(temp, final); }
            catch (IOException) when (File.Exists(final)) { /* Bên gửi khác hoàn tất cùng outbox ID */ }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
