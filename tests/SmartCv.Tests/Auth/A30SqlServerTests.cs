using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartCv.Core.Entities;
using SmartCv.Core.Contracts;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Auth;

public class A30SqlServerTests
{
    [A30SqlFact]
    public async Task Approved_empty_SQL_catalog_preserves_history_and_last_Admin_across_independent_API_instances()
    {
        var target = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A30SqlFact.Connection)!);
        A30SqlFact.Validate(target);
        await using (var connection = new SqlConnection(target.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0", connection);
            Assert.Equal(0, (int)(await command.ExecuteScalarAsync())!); // Never reuse/reset a populated test catalog.
        }
        var options = new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(target.ConnectionString).Options;
        await using var db = new SmartCvDbContext(options);
        await db.Database.MigrateAsync(); // Only this pre-provisioned, explicitly approved empty GUID catalog.
        User Synthetic(string role) => new() { Email = Guid.NewGuid().ToString("N") + "@example.invalid", FullName = "Synthetic A30 SQL", Role = role,
            IsActive = true, CreatedAt = DateTime.UtcNow, PasswordHash = "temporary-placeholder-replaced-before-save" };
        var first = Synthetic("Admin"); var second = Synthetic("Admin"); var hr = Synthetic("HR");
        foreach (var user in new[] { first, second, hr }) user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<User>().HashPassword(user, A28TestSupport.Password());
        db.AddRange(first, second, hr); await db.SaveChangesAsync();
        WebApplicationFactory<Program> Factory() => A28TestSupport.Factory(new() { ["Scoring:WorkerEnabled"] = "false", ["Retention:AutoAnonymize"] = "false" })
            .WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            {
                s.RemoveAll<DbContextOptions<SmartCvDbContext>>(); s.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                s.AddDbContext<SmartCvDbContext>(o => o.UseSqlServer(target.ConnectionString));
            }));
        using var factoryA = Factory(); using var factoryB = Factory();
        using var a = A29AuthorizationTests.Client(factoryA, first); using var b = A29AuthorizationTests.Client(factoryB, second);
        var results = await Task.WhenAll(a.PostAsync($"/api/users/{second.UserId}/lock", null), b.PostAsync($"/api/users/{first.UserId}/lock", null));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.True(r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Conflict));
        Assert.Equal(1, await db.Users.CountAsync(u => u.IsActive && u.Role == "Admin"));
        foreach (var result in results) result.Dispose();
        var survivor = await db.Users.AsNoTracking().SingleAsync(u => u.IsActive && u.Role == "Admin");
        using var managerA = A29AuthorizationTests.Client(factoryA, survivor); using var managerB = A29AuthorizationTests.Client(factoryB, survivor);
        var email = Guid.NewGuid().ToString("N") + "@example.invalid";
        var created = await Task.WhenAll(A30AccountEndpointTests.Create(managerA, email), A30AccountEndpointTests.Create(managerB, email.ToUpperInvariant()));
        Assert.Equal(1, created.Count(r => r.StatusCode == HttpStatusCode.Created)); Assert.Equal(1, created.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        foreach (var result in created) result.Dispose();
        var application = Applications.ApplicationStageEndpointTests.NewApplication(); db.Applications.Add(application); await db.SaveChangesAsync();
        db.ApplicationEvents.Add(new() { ApplicationId = application.ApplicationId, UserId = hr.UserId, FromStage = "New", ToStage = "Reviewing", Actor = "Original SQL history", Note = "Original SQL note" });
        await db.SaveChangesAsync(); var legacy = await db.ApplicationEvents.AsNoTracking().SingleAsync();
        using var oldHr = A29AuthorizationTests.Client(factoryA, hr);
        using var reset = await managerA.PostAsync($"/api/users/{hr.UserId}/reset-password", null); Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldHr.GetAsync("/api/auth/me")).StatusCode);
        var preserved = await db.ApplicationEvents.AsNoTracking().SingleAsync();
        Assert.Equal((legacy.UserId, legacy.Actor, legacy.Note, legacy.CreatedAt), (preserved.UserId, preserved.Actor, preserved.Note, preserved.CreatedAt));
        foreach (var sql in new[] { "UPDATE ApplicationEvents SET Note=N'blocked'", "DELETE FROM ApplicationEvents" })
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(sql)); Assert.Equal(51000, error.Number);
        }
        Assert.Equal(1, await db.ApplicationEvents.CountAsync());
        // Retain the approved test catalog and its synthetic rows for review. No CREATE/DROP DATABASE or cleanup.
    }

    [Theory]
    [InlineData("Server=never-connect.invalid;Database=SmartCvDb;Integrated Security=true")]
    [InlineData("Server=never-connect.invalid;Database=master;Integrated Security=true")]
    [InlineData("Server=never-connect.invalid;Database=SmartCv_A30_Test_old;Integrated Security=true")]
    public void SQL_opt_in_rejects_application_system_and_non_GUID_catalogs_without_opening(string connection) =>
        Assert.Throws<ArgumentException>(() => A30SqlFact.Validate(new(connection)));
}

public sealed class A30SqlFact : FactAttribute
{
    public const string Connection = "SMARTCV_A30_TEST_SQL_CONNECTION";
    public const string Approval = "SMARTCV_A30_SQL_APPROVED";
    public A30SqlFact()
    {
        if (Environment.GetEnvironmentVariable(Approval) != "1" || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Connection)))
        { Skip = "NOT RUN: requires separate user approval and a pre-provisioned empty GUID A30 SQL catalog; never creates or deletes a catalog."; return; }
        try { Validate(new(Environment.GetEnvironmentVariable(Connection)!)); }
        catch (ArgumentException) { Skip = "NOT RUN: SQL target is not an explicitly designated A30 GUID test catalog."; }
    }
    internal static void Validate(SqlConnectionStringBuilder target)
    {
        if (string.IsNullOrWhiteSpace(target.DataSource) || !string.IsNullOrWhiteSpace(target.AttachDBFilename)
            || !System.Text.RegularExpressions.Regex.IsMatch(target.InitialCatalog, "^SmartCv_A30_Test_[a-f0-9]{32}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ArgumentException("A30 requires a designated GUID test catalog.");
    }
}