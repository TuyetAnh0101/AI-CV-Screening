using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using SmartCv.Core.Email;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Email;

public sealed class EmailQueue(SmartCvDbContext db, EmailTemplates templates,
    IDataProtectionProvider provider, TimeProvider clock) : IEmailQueue
{
    public const string Purpose = "SmartCv.EmailOutbox.v1";
    private readonly IDataProtector protector = provider.CreateProtector(Purpose);

    public Task EnqueueAsync(int? userId, EmailTemplate template, EmailTemplateModel model,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (userId is <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        var message = templates.Render(template, model);
        var serialized = JsonSerializer.Serialize(message);
        var now = clock.GetUtcNow().UtcDateTime;
        db.EmailOutbox.Add(new EmailOutboxItem
        {
            UserId = userId,
            ToAddress = message.ToAddress,
            Template = template.ToString(),
            ProtectedPayload = protector.Protect(serialized),
            Status = "Pending",
            Attempts = 0,
            NextAttemptAt = now,
            CreatedAt = now
        });
        // Không SaveChanges: gọi Enqueue trong cùng DbContext và transaction nghiệp vụ.
        return Task.CompletedTask;
    }
}
