using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Auth;

public class A29SqlServerTests
{
    [A29SqlFact]
    public async Task Real_SQL_preserves_legacy_history_attributes_HTTP_events_and_keeps_A35_trigger_and_conflicts()
    {
        var target = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A29SqlFact.Setting)!);
        A29SqlFact.Validate(target);
        // Only a brand-new GUID catalog on the explicitly opted-in test server. Retain it for review.
        var catalog = "SmartCv_A29_Test_" + Guid.NewGuid().ToString("N");
        var master = new SqlConnectionStringBuilder(target.ConnectionString) { InitialCatalog = "master" };
        await using (var connection = new SqlConnection(master.ConnectionString))
        {
            await connection.OpenAsync();
            await using var exists = new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE name=@catalog", connection);
            exists.Parameters.AddWithValue("@catalog", catalog);
            Assert.Equal(0, (int)(await exists.ExecuteScalarAsync())!);
            await using var create = new SqlCommand($"CREATE DATABASE [{catalog}]", connection); await create.ExecuteNonQueryAsync();
        }
        target.InitialCatalog = catalog;
        var dbOptions = new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(target.ConnectionString).Options;
        await using var db = new SmartCvDbContext(dbOptions);
        await db.GetService<IMigrator>().MigrateAsync(A27MigrationTests.Previous);
        var legacyCv = Guid.NewGuid(); var hash = Guid.NewGuid().ToString("N").PadRight(64, '0');
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Cvs(CvId,OriginalFileName,StoredPath,ContentType,FileSizeBytes,FileHash) VALUES({legacyCv},N'A29-synthetic.pdf',N'synthetic',N'application/pdf',1,{hash})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Applications(CvId,JobId) VALUES({legacyCv},1)");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO ApplicationEvents(ApplicationId,FromStage,ToStage,Actor,Note) SELECT ApplicationId,N'New',N'Reviewing',N'Legacy actor',N'Original history' FROM Applications");
        await db.Database.MigrateAsync();
        var legacy = await db.ApplicationEvents.AsNoTracking().SingleAsync(); Assert.Null(legacy.UserId);

        using var factory = A28TestSupport.Factory(new() { ["Scoring:WorkerEnabled"] = "false" }).WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<DbContextOptions<SmartCvDbContext>>(); s.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
            s.AddDbContext<SmartCvDbContext>(o => o.UseSqlServer(target.ConnectionString));
        }));
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        using var http = A29AuthorizationTests.Client(factory, user);
        var id = await db.Applications.Select(a => a.ApplicationId).SingleAsync();
        var originalVersion = await db.Applications.Select(a => a.StageVersion).SingleAsync();
        using var changed = await http.PatchAsJsonAsync($"/api/applications/{id}/stage", new { toStage = "Interview", actor = "FORGED", userId = 999 });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var conflict = await http.PostAsJsonAsync("/api/applications/stage", new
        { applicationIds = new[] { id }, toStage = "Reviewing", expectedStageVersions = new Dictionary<int, Guid> { [id] = originalVersion }, actor = "FORGED" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var events = await db.ApplicationEvents.AsNoTracking().OrderBy(e => e.EventId).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal((legacy.Actor, legacy.Note, legacy.CreatedAt, legacy.FromStage, legacy.ToStage),
            (events[0].Actor, events[0].Note, events[0].CreatedAt, events[0].FromStage, events[0].ToStage));
        Assert.Null(events[0].UserId); Assert.Equal(user.UserId, events[1].UserId); Assert.Equal(user.FullName, events[1].Actor);
        foreach (var sql in new[] { "UPDATE ApplicationEvents SET Note=N'forged'", "DELETE FROM ApplicationEvents" })
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(sql)); Assert.Equal(51000, error.Number);
        }
        Assert.Equal(2, await db.ApplicationEvents.CountAsync());
        await A28TestSupport.WithDb(factory, async context => { var saved = await context.Users.SingleAsync(); saved.IsActive = false; await context.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/jobs")).StatusCode);
    }
}

public sealed class A29SqlFact : FactAttribute
{
    public const string Setting = "SMARTCV_A29_TEST_SQL_CONNECTION";
    public A29SqlFact()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Setting))) { Skip = "NOT RUN: explicitly opt in to an isolated A29 SQL test server/catalog; new GUID catalog is retained."; return; }
        try { Validate(new(Environment.GetEnvironmentVariable(Setting)!)); }
        catch (ArgumentException) { Skip = "NOT RUN: A29 SQL requires an explicitly designated test catalog without attached files."; }
    }
    internal static void Validate(SqlConnectionStringBuilder target)
    {
        if (string.IsNullOrWhiteSpace(target.DataSource) || !target.InitialCatalog.StartsWith("SmartCv_A29_Test_", StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(target.AttachDBFilename)) throw new ArgumentException("Only designated A29 test catalogs are accepted.");
    }
}
