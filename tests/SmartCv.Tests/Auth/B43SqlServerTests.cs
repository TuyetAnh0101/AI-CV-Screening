using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Auth;

public class B43SqlServerTests
{
    [B43SqlFact]
    public async Task Approved_existing_empty_catalog_proves_append_only_trigger_and_atomic_credential_rollback()
    {
        var target = B43SqlFact.Target(Environment.GetEnvironmentVariable(B43SqlFact.Connection)!);
        await using var connection = new SqlConnection(target.ConnectionString); await connection.OpenAsync();
        await using (var check = new SqlCommand("SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0", connection))
            Assert.Equal(0, (int)(await check.ExecuteScalarAsync())!);
        var options = new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(target.ConnectionString).Options;
        await using var db = new SmartCvDbContext(options);
        // Generate schema only, execute on the already open, approved catalog. No DatabaseCreator/Create/Drop calls.
        var sql = db.GetService<IMigrator>().GenerateScript(toMigration: "20261120043759_AddAuditEvents");
        Assert.DoesNotMatch(@"(?i)\b(CREATE|DROP)\s+DATABASE\b|(?im)^\s*USE\s+", sql);
        foreach (var batch in Regex.Split(sql, @"(?im)^\s*GO\s*\r?$").Where(s => !string.IsNullOrWhiteSpace(s)))
        { await using var command = new SqlCommand(batch, connection) { CommandTimeout = 120 }; await command.ExecuteNonQueryAsync(); }
        var password = A28TestSupport.Password();
        var user = new User { Email = Guid.NewGuid().ToString("N") + "@example.invalid", FullName = "Synthetic B43 SQL", Role = "HR", IsActive = true, CreatedAt = DateTime.UtcNow };
        user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<User>().HashPassword(user, password);
        db.Users.Add(user); await db.SaveChangesAsync();
        var row = new AuditEvent { OccurredAtUtc = DateTime.UtcNow, ActorUserId = user.UserId, ActorNameSnapshot = user.FullName,
            ActorRoleSnapshot = user.Role, ActionCode = "auth.login", Category = "Authentication", ResourceType = "User",
            ResourceId = user.UserId.ToString(), Outcome = "Succeeded", RequestId = "synthetic-sql", SafeSummary = "Synthetic event" };
        db.AuditEvents.Add(row); await db.SaveChangesAsync();
        foreach (var statement in new[] { "UPDATE dbo.AuditEvents SET SafeSummary=N'blocked'", "DELETE FROM dbo.AuditEvents" })
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(statement)); Assert.Equal(51043, error.Number);
        }
        Assert.Equal(1, await db.AuditEvents.CountAsync());
        await using (var trigger = new SqlCommand("SELECT is_disabled FROM sys.triggers WHERE name=N'TR_ApplicationEvents_NoChange'", connection))
            Assert.False((bool)(await trigger.ExecuteScalarAsync())!);

        var fault = new FailAudit();
        using var factory = B43AuditTests.Factory().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<DbContextOptions<SmartCvDbContext>>(); s.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
            s.AddDbContext<SmartCvDbContext>(o => o.UseSqlServer(target.ConnectionString).AddInterceptors(fault));
        }));
        using var http = A29AuthorizationTests.Client(factory, user);
        var original = A30AccountEndpointTests.Fingerprint(user); fault.Enabled = true;
        using var change = await http.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = password, newPassword = A28TestSupport.Password() });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, change.StatusCode);
        var unchanged = await db.Users.AsNoTracking().SingleAsync(u => u.UserId == user.UserId);
        Assert.True(original == A30AccountEndpointTests.Fingerprint(unchanged)); Assert.Equal(1, await db.AuditEvents.CountAsync());
        // Retain all synthetic data for review; never clean or drop the provided catalog.
    }

    [Theory]
    [InlineData("Server=never-connect.invalid;Database=SmartCvDb;Integrated Security=true")]
    [InlineData("Server=never-connect.invalid;Database=master;Integrated Security=true")]
    [InlineData("Server=never-connect.invalid;Database=SmartCv_B43_Test_old;Integrated Security=true")]
    public void SQL_target_guard_rejects_application_and_non_GUID_catalogs_before_opening(string text) =>
        Assert.Throws<ArgumentException>(() => B43SqlFact.Target(text));

    private sealed class FailAudit : SaveChangesInterceptor
    {
        internal bool Enabled;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<AuditEvent>().Any(e => e.State == EntityState.Added)) throw new DbUpdateException("Synthetic audit failure");
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class B43SqlFact : FactAttribute
{
    public const string Connection = "SMARTCV_B43_TEST_SQL_CONNECTION";
    public B43SqlFact()
    {
        if (Environment.GetEnvironmentVariable("SMARTCV_B43_SQL_APPROVED") != "1" || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Connection)))
            Skip = "NOT VERIFIED: separate approval and a pre-provisioned empty GUID B43 SQL catalog are required. Never creates or drops a catalog.";
    }
    internal static SqlConnectionStringBuilder Target(string text)
    {
        SqlConnectionStringBuilder target;
        try { target = new(text); } catch (ArgumentException) { throw new ArgumentException("Invalid B43 SQL test target."); }
        if (string.IsNullOrWhiteSpace(target.DataSource) || !string.IsNullOrWhiteSpace(target.AttachDBFilename)
            || !Regex.IsMatch(target.InitialCatalog, "^SmartCv_B43_Test_[a-f0-9]{32}$", RegexOptions.CultureInvariant)) throw new ArgumentException("Invalid B43 SQL test target.");
        return target;
    }
}
