using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartCv.Infrastructure.Candidates;
using SmartCv.Infrastructure.Data;
using SmartCv.Tests.Jobs;

namespace SmartCv.Tests.Candidates;

/// <summary>Explicit opt-in only. Creates and retains a new GUID test catalog; never migrates an existing catalog.</summary>
public sealed class A24SqlFactAttribute : FactAttribute
{
    internal const string Setting = "SMARTCV_A24_TEST_SQL_CONNECTION";
    internal const string Prefix = "SmartCv_A24_Test_";
    public A24SqlFactAttribute()
    {
        var value = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrWhiteSpace(value)) { Skip = "NOT RUN: A24 SQL requires an explicitly supplied isolated test connection."; return; }
        try { Validate(new SqlConnectionStringBuilder(value)); }
        catch (ArgumentException) { Skip = "NOT RUN: A24 only accepts its own test prefix and no attached files."; }
    }
    internal static void Validate(SqlConnectionStringBuilder value)
    {
        if (!value.InitialCatalog.StartsWith(Prefix, StringComparison.Ordinal) || !string.IsNullOrEmpty(value.AttachDBFilename))
            throw new ArgumentException("An isolated A24 test catalog is required.");
    }
}

internal sealed class A24SqlEnvironment(string connection)
{
    internal SmartCvDbContext Context(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options);
    }

    internal static async Task<A24SqlEnvironment> CreateAsync()
    {
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A24SqlFactAttribute.Setting));
        A24SqlFactAttribute.Validate(builder);
        builder.InitialCatalog = A24SqlFactAttribute.Prefix + Guid.NewGuid().ToString("N");
        builder.Pooling = false; builder.ConnectTimeout = 10;
        Assert.Matches("^SmartCv_A24_Test_[0-9a-f]{32}$", builder.InitialCatalog);
        var master = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" };
        await using (var sql = new SqlConnection(master.ConnectionString))
        {
            await sql.OpenAsync();
            await using var check = new SqlCommand("SELECT DB_ID(@name);", sql);
            check.Parameters.AddWithValue("@name", builder.InitialCatalog);
            Assert.Equal(DBNull.Value, await check.ExecuteScalarAsync());
            await using var create = new SqlCommand($"CREATE DATABASE [{builder.InitialCatalog}];", sql);
            await create.ExecuteNonQueryAsync();
        }
        var directory = TestSupport.RepoFile("artifacts/a24/sql");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, builder.InitialCatalog + ".txt"), "Retained test catalog: " + builder.InitialCatalog);
        var environment = new A24SqlEnvironment(builder.ConnectionString);
        await using var db = environment.Context();
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        return environment;
    }

    internal WebApplicationFactory<Program> Api(IInterceptor? interceptor = null) =>
        JobCrudEndpointTests.Factory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<SmartCvDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
            services.AddDbContext<SmartCvDbContext>(o =>
            {
                o.UseSqlServer(connection);
                if (interceptor is not null) o.AddInterceptors(interceptor);
            });
        }));
}

public class CandidateSqlServerTests
{
    [A24SqlFact]
    public async Task Concurrent_creates_in_two_API_hosts_have_one_winner_and_one_conflict()
    {
        var env = await A24SqlEnvironment.CreateAsync();
        using var first = env.Api(); using var second = env.Api();
        using var a = first.CreateClient(); using var b = second.CreateClient();
        var body = new { fullName = "Synthetic Candidate", email = "synthetic@example.com" };
        var responses = await Task.WhenAll(a.PostAsJsonAsync("/api/candidates", body), b.PostAsJsonAsync("/api/candidates", body));
        try { Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, responses.Select(r => r.StatusCode).Order()); }
        finally { foreach (var response in responses) response.Dispose(); }
        await using var db = env.Context();
        Assert.Equal(1, await db.Candidates.CountAsync());
    }

    [A24SqlFact]
    public async Task Failure_after_SQL_SaveChanges_rolls_back_creation_assignment_and_orphan_deletion()
    {
        var env = await A24SqlEnvironment.CreateAsync();
        Guid cvId; int oldId;
        await using (var db = env.Context())
        {
            var cv = JobCrudEndpointTests.Cv();
            cv.Candidate = new SmartCv.Core.Entities.Candidate { FullName = "Old Synthetic", Email = "old@example.com" };
            db.Cvs.Add(cv); await db.SaveChangesAsync(); cvId = cv.CvId; oldId = cv.CandidateId!.Value;
        }
        using var factory = env.Api(new FailAfterSave()); using var client = factory.CreateClient();
        using var response = await client.PutAsJsonAsync($"/api/cvs/{cvId}/candidate", new { fullName = "New Synthetic", email = "new@example.com" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("PRIVATE", await response.Content.ReadAsStringAsync());
        await using var verify = env.Context();
        Assert.Equal(oldId, await verify.Cvs.Where(c => c.CvId == cvId).Select(c => c.CandidateId).SingleAsync());
        Assert.Equal(1, await verify.Candidates.CountAsync());
    }

    [A24SqlFact]
    public async Task Merge_preserves_existing_HR_history_scores_and_supports_relational_detail_queries()
    {
        var env = await A24SqlEnvironment.CreateAsync();
        int sourceId; int targetId; int eventCount; decimal?[] scores;
        await using (var db = env.Context())
        {
            var seed = await A23CvListEndpointTests.SeedAsync(db);
            var source = await db.Cvs.Include(c => c.Candidate).SingleAsync(c => c.CvId == seed.Cv["A"]);
            var target = await db.Cvs.Include(c => c.Candidate).SingleAsync(c => c.CvId == seed.Cv["B"]);
            sourceId = source.CandidateId!.Value; targetId = target.CandidateId!.Value;
            eventCount = await db.ApplicationEvents.CountAsync();
            scores = await db.Evaluations.OrderBy(e => e.EvaluationId).Select(e => e.FinalScore).ToArrayAsync();
        }
        using var factory = env.Api(); using var client = factory.CreateClient();
        using var merged = await client.PostAsJsonAsync($"/api/candidates/{sourceId}/merge", new { targetCandidateId = targetId, actor = "synthetic" });
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        using var detail = await client.GetAsync($"/api/candidates/{targetId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var json = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal(2, json.RootElement.GetProperty("cvs").GetArrayLength());
        Assert.Equal(3, json.RootElement.GetProperty("applications").GetArrayLength());
        await using var verify = env.Context();
        Assert.False(await verify.Candidates.AnyAsync(c => c.CandidateId == sourceId));
        Assert.Equal(eventCount, await verify.ApplicationEvents.CountAsync());
        Assert.Equal(scores, await verify.Evaluations.OrderBy(e => e.EvaluationId).Select(e => e.FinalScore).ToArrayAsync());
    }

    [A24SqlFact]
    public async Task Cancellation_while_waiting_for_the_database_write_lock_leaves_no_write_and_releases_transaction()
    {
        var env = await A24SqlEnvironment.CreateAsync();
        await using var first = env.Context(); await using var second = env.Context();
        await using (var held = await CandidateWriteScope.BeginAsync(first, default))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await CandidateWriteScope.BeginAsync(second, cancellation.Token));
        }
        await using var available = await CandidateWriteScope.BeginAsync(second, default);
        Assert.Equal(0, await second.Candidates.CountAsync());
    }

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new InvalidOperationException("PRIVATE synthetic SQL failure"));
    }
}
