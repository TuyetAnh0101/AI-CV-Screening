using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartCv.Core.Email;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Email;

namespace SmartCv.Tests.Email;

public sealed class A55QueueTests
{
    [Fact]
    public async Task PayloadDoesNotStoreReadableToken()
    {
        var options = new DbContextOptionsBuilder<SmartCvDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using var db = new SmartCvDbContext(options);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string,string?> { ["App:WebBaseUrl"] = "https://scapp.test" }).Build();
        var provider = new EphemeralDataProtectionProvider();
        var queue = new EmailQueue(db, new EmailTemplates(cfg), provider, TimeProvider.System);
        await queue.EnqueueAsync(null, EmailTemplate.VerifyEmail,
            new EmailTemplateModel("sample@example.test", "B", "/verify?token=SECRET_TOKEN_FOR_TEST"));
        // Enqueue không tự SaveChanges: transaction của caller mới quyết định commit.
        Assert.Equal(1, db.ChangeTracker.Entries<SmartCv.Core.Entities.EmailOutboxItem>()
            .Count(e => e.State == EntityState.Added));
        await db.SaveChangesAsync();
        var row = await db.EmailOutbox.SingleAsync();
        Assert.Equal("Pending", row.Status);
        Assert.NotNull(row.ProtectedPayload);
        Assert.DoesNotContain("SECRET_TOKEN_FOR_TEST", row.ProtectedPayload);
        Assert.DoesNotContain("https://scapp.test", row.ProtectedPayload);
    }
}
