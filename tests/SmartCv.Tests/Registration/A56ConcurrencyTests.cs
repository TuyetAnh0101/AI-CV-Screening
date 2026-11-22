using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Registration;

/// <summary>
/// Contract/index assertions only. Real concurrent HTTP+SQL tests are in docs and need an isolated SQL catalog.
/// These tests must NOT be represented as proof that multi-process registration is safe.
/// </summary>
public sealed class A56ConcurrencyTests
{
    [Fact]
    public void DatabaseHasUniqueUserEmailAndUniqueTokenHash()
    {
        var options = new DbContextOptionsBuilder<SmartCvDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new SmartCvDbContext(options);
        var user = db.Model.FindEntityType(typeof(User))!;
        Assert.Contains(user.GetIndexes(), i => i.IsUnique && i.Properties.Select(p=>p.Name).SequenceEqual(new[] { "Email" }));
        var token = db.Model.FindEntityType(typeof(UserToken))!;
        Assert.Contains(token.GetIndexes(), i => i.IsUnique && i.Properties.Select(p=>p.Name).SequenceEqual(new[] { "TokenHash" }));
    }

    [Fact]
    public void DbContextHasOneUserTokensDbSet()
    {
        var property = typeof(SmartCvDbContext).GetProperty("UserTokens");
        Assert.NotNull(property);
    }
}
