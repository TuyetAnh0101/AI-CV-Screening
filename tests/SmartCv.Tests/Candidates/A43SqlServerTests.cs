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
using SmartCv.Core.Storage;
using SmartCv.Infrastructure.Privacy;
using SmartCv.Infrastructure.Candidates;
using SmartCv.Infrastructure.Data;
using SmartCv.Tests.Jobs;

namespace SmartCv.Tests.Candidates;

/// <summary>Explicit opt-in only. Creates and retains a new GUID test catalog; never migrates an existing catalog.</summary>
public sealed class A43SqlFactAttribute : FactAttribute
{
    internal const string Setting = "SMARTCV_A43_TEST_SQL_CONNECTION";
    internal const string Prefix = "SmartCv_A43_Test_";
    public A43SqlFactAttribute()
    {
        var value = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrWhiteSpace(value)) { Skip = "NOT RUN: A43 SQL requires an explicitly supplied isolated test connection."; return; }
        try { Validate(new SqlConnectionStringBuilder(value)); }
        catch (ArgumentException) { Skip = "NOT RUN: A43 only accepts its own test prefix and no attached files."; }
    }
    internal static void Validate(SqlConnectionStringBuilder value)
    {
        if (!value.InitialCatalog.StartsWith(Prefix, StringComparison.Ordinal) || !string.IsNullOrEmpty(value.AttachDBFilename))
            throw new ArgumentException("An isolated A24 test catalog is required.");
    }
}

internal sealed class A43SqlEnvironment(string connection)
{
    internal SmartCvDbContext Context(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options);
    }

    internal static async Task<A43SqlEnvironment> CreateAsync()
    {
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A43SqlFactAttribute.Setting));
        A43SqlFactAttribute.Validate(builder);
        builder.InitialCatalog = A43SqlFactAttribute.Prefix + Guid.NewGuid().ToString("N");
        builder.Pooling = false; builder.ConnectTimeout = 10;
        Assert.Matches("^SmartCv_A43_Test_[0-9a-f]{32}$", builder.InitialCatalog);
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
        var directory = TestSupport.RepoFile("artifacts/a43/sql");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, builder.InitialCatalog + ".txt"), "Retained test catalog: " + builder.InitialCatalog);
        var environment = new A43SqlEnvironment(builder.ConnectionString);
        await using var db = environment.Context();
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        return environment;
    }

    internal WebApplicationFactory<Program> Api(IInterceptor? interceptor = null, ICvFileStorage? storage = null) =>
        JobCrudEndpointTests.Factory(clock: new JobClock { Now = new DateTimeOffset(A43EndpointTests.Now.AddHours(1)) }).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            if (storage is not null) { services.RemoveAll<ICvFileStorage>(); services.AddSingleton<ICvFileStorage>(storage); }
            services.RemoveAll<DbContextOptions<SmartCvDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
            services.AddDbContext<SmartCvDbContext>(o =>
            {
                o.UseSqlServer(connection);
                if (interceptor is not null) o.AddInterceptors(interceptor);
            });
        }));
}


public class A43SqlServerTests
{
    [A43SqlFact]
    public async Task Export_and_erasure_preserve_scores_labels_and_history_but_remove_all_candidate_contact_and_files()
    {
        var env = await A43SqlEnvironment.CreateAsync();
        var files = TestSupport.Storage(TestSupport.TempRoot());
        A43EndpointTests.Seed seed;
        await using (var db = env.Context()) seed = await A43EndpointTests.SeedAsync(db, files);
        string[] paths;
        await using (var db = env.Context()) paths = await db.Cvs.Select(c => c.StoredPath).ToArrayAsync();
        using var factory = env.Api(storage: files); using var client = factory.CreateClient();
        using var export = await client.GetAsync($"/api/candidates/{seed.CandidateId}/data-export?handledBy=hr.test");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using var response = await client.PostAsJsonAsync($"/api/candidates/{seed.CandidateId}/erase", new { confirm = true, handledBy = "hr.test" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, result.RootElement.GetProperty("cvsAnonymized").GetInt32());
        Assert.Equal(1, result.RootElement.GetProperty("applicationsWithdrawn").GetInt32());
        Assert.True(result.RootElement.GetProperty("candidateDeleted").GetBoolean());
        await using (var db = env.Context())
        {
            Assert.Empty(await db.Candidates.ToListAsync());
            Assert.Equal(2, await db.CandidateDataRequests.CountAsync()); // Export + Erase survive without FK
            var cvs = await db.Cvs.ToListAsync();
            Assert.All(cvs, cv =>
            {
                Assert.Null(cv.CandidateId); Assert.Null(cv.ExtractedText); Assert.Null(cv.ExtractionError); Assert.NotNull(cv.AnonymizedAt);
                Assert.Equal(CvAnonymizer.RemovedPath, cv.StoredPath); Assert.Equal(CvAnonymizer.AnonymizedHash(cv.CvId), cv.FileHash);
            });
            var score = await db.Evaluations.SingleAsync();
            Assert.Equal(87, score.FinalScore); Assert.Null(score.Reasoning); Assert.Null(score.AiRawResponse); Assert.Null(score.ErrorMessage);
            Assert.Null((await db.EvaluationSkills.SingleAsync()).Evidence);
            Assert.Equal(1, await db.HrLabels.CountAsync());
            Assert.Equal("MissingSkills", (await db.Applications.FindAsync(seed.Applications[1]))!.RejectReason);
            var decision = await db.ApplicationEvents.SingleAsync(e => e.Note == SmartCv.Core.Candidates.CandidateErasePlan.EventNote);
            Assert.Equal("CandidateWithdrew", decision.RejectReason); Assert.Equal("hr.test", decision.Actor);
            Assert.Equal(2, await db.ApplicationEvents.CountAsync());
        }
        Assert.All(paths, path => Assert.False(files.Exists(path))); Assert.Empty(files.PendingErasureIds(200));
        using var repeated = await client.PostAsJsonAsync($"/api/candidates/{seed.CandidateId}/erase", new { confirm = true, handledBy = "hr.test" });
        Assert.Equal(HttpStatusCode.Gone, repeated.StatusCode);
        foreach (var query in new[] { "/api/cvs?search=thu.xoa@example.com", "/api/cvs?search=0912000111", "/api/candidates?search=thu.xoa" })
        {
            using var json = JsonDocument.Parse(await client.GetStringAsync(query));
            Assert.Equal(0, json.RootElement.GetProperty("totalCount").GetInt32());
        }
        using var file = await client.GetAsync($"/api/cvs/{seed.Cvs[0]}/file");
        Assert.Equal(HttpStatusCode.Gone, file.StatusCode);
    }

    [A43SqlFact]
    public async Task File_failure_during_second_preparation_rolls_back_all_SQL_and_restores_both_files()
    {
        var env = await A43SqlEnvironment.CreateAsync();
        var files = new A43SafetyTests.FaultingFiles(TestSupport.Storage(TestSupport.TempRoot())) { FailAfterPrepare = 2 };
        A43EndpointTests.Seed seed;
        await using (var db = env.Context()) seed = await A43EndpointTests.SeedAsync(db, files);
        using var factory = env.Api(storage: files); using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync($"/api/candidates/{seed.CandidateId}/erase", new { confirm = true, handledBy = "hr.test" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode); Assert.DoesNotContain("PRIVATE", await response.Content.ReadAsStringAsync());
        await VerifyRollbackAsync(env, seed, files);
    }

    [A43SqlFact]
    public async Task Audit_save_failure_rolls_back_tombstones_contact_deletion_decisions_and_files()
    {
        var env = await A43SqlEnvironment.CreateAsync();
        var files = TestSupport.Storage(TestSupport.TempRoot());
        A43EndpointTests.Seed seed;
        await using (var db = env.Context()) seed = await A43EndpointTests.SeedAsync(db, files);
        using var factory = env.Api(new FailAuditSave(), files); using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync($"/api/candidates/{seed.CandidateId}/erase", new { confirm = true, handledBy = "hr.test" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await VerifyRollbackAsync(env, seed, files);
    }

    [A43SqlFact]
    public async Task Two_API_hosts_erasing_the_same_candidate_have_one_success_one_410_and_one_erase_audit()
    {
        var env = await A43SqlEnvironment.CreateAsync();
        var files = TestSupport.Storage(TestSupport.TempRoot());
        A43EndpointTests.Seed seed;
        await using (var db = env.Context()) seed = await A43EndpointTests.SeedAsync(db, files);
        using var first = env.Api(storage: files); using var second = env.Api(storage: files);
        using var a = first.CreateClient(); using var b = second.CreateClient();
        var body = new { confirm = true, handledBy = "hr.test" };
        var responses = await Task.WhenAll(a.PostAsJsonAsync($"/api/candidates/{seed.CandidateId}/erase", body), b.PostAsJsonAsync($"/api/candidates/{seed.CandidateId}/erase", body));
        try { Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Gone }, responses.Select(r => r.StatusCode).Order()); }
        finally { foreach (var response in responses) response.Dispose(); }
        await using var verify = env.Context();
        Assert.Equal(1, await verify.CandidateDataRequests.CountAsync(r => r.RequestType == "Erase"));
        Assert.Equal(2, await verify.ApplicationEvents.CountAsync());
    }

    [A43SqlFact]
    public async Task Existing_personal_data_reader_blocks_erasure_and_timeout_does_not_write()
    {
        var env = await A43SqlEnvironment.CreateAsync();
        var files = TestSupport.Storage(TestSupport.TempRoot());
        A43EndpointTests.Seed seed;
        await using (var db = env.Context()) seed = await A43EndpointTests.SeedAsync(db, files);
        await using var reader = env.Context();
        await using var lease = await CvPrivacyScope.BeginAsync(reader, false, default);
        using var factory = env.Api(storage: files); using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync($"/api/candidates/{seed.CandidateId}/erase", new { confirm = true, handledBy = "hr.test" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await VerifyRollbackAsync(env, seed, files);
    }

    [A43SqlFact]
    public async Task Cancellation_after_withdrawal_SQL_restores_all_state_without_retry()
    {
        var env = await A43SqlEnvironment.CreateAsync();
        var files = TestSupport.Storage(TestSupport.TempRoot());
        A43EndpointTests.Seed seed;
        await using (var db = env.Context()) seed = await A43EndpointTests.SeedAsync(db, files);
        using var cancellation = new CancellationTokenSource();
        using var factory = env.Api(new CancelAfterSave(cancellation), files); using var client = factory.CreateClient();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.PostAsJsonAsync($"/api/candidates/{seed.CandidateId}/erase", new { confirm = true, handledBy = "hr.test" }, cancellation.Token));
        // Wait for the request's SQL rollback by acquiring the same cross-process privacy lock.
        await using (var verifyLock = env.Context())
        {
            await using var privacy = await CvPrivacyScope.BeginAsync(verifyLock, true, default);
        }
        await VerifyRollbackAsync(env, seed, files);
    }

    [A43SqlFact]
    public async Task Ambiguous_commit_error_keeps_journals_and_A39_recovery_reconciles_the_rolled_back_database()
    {
        var env = await A43SqlEnvironment.CreateAsync();
        var files = TestSupport.Storage(TestSupport.TempRoot());
        A43EndpointTests.Seed seed;
        await using (var db = env.Context()) seed = await A43EndpointTests.SeedAsync(db, files);
        using var factory = env.Api(new CommitFailure(), files); using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync($"/api/candidates/{seed.CandidateId}/erase", new { confirm = true, handledBy = "hr.test" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(2, files.PendingErasureIds(200).Count);
        await using var recoveryDb = env.Context();
        var anonymizer = new CvAnonymizer(recoveryDb, files, Microsoft.Extensions.Logging.Abstractions.NullLogger<CvAnonymizer>.Instance);
        foreach (var id in seed.Cvs)
        {
            var recovery = await anonymizer.AnonymizeAsync(id, "synthetic", true, DateTime.UtcNow, default, recoveryOnly: true);
            Assert.Equal(AnonymizeOutcome.Recovered, recovery.Outcome);
        }
        await VerifyRollbackAsync(env, seed, files);
    }

    private static async Task VerifyRollbackAsync(A43SqlEnvironment env, A43EndpointTests.Seed seed, ICvFileStorage files)
    {
        await using var db = env.Context();
        Assert.Equal(1, await db.Candidates.CountAsync()); Assert.Empty(await db.CandidateDataRequests.ToListAsync());
        Assert.Equal("Interview", (await db.Applications.FindAsync(seed.Applications[0]))!.Stage);
        Assert.Equal(1, await db.ApplicationEvents.CountAsync());
        var cvs = await db.Cvs.ToListAsync();
        Assert.All(cvs, cv => { Assert.Null(cv.AnonymizedAt); Assert.NotNull(cv.ExtractedText); Assert.True(files.Exists(cv.StoredPath)); });
        Assert.Empty(((ICvErasureStorage)files).PendingErasureIds(200));
    }

    private sealed class FailAuditSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default) =>
            data.Context!.ChangeTracker.Entries<SmartCv.Core.Entities.CandidateDataRequest>().Any(e => e.State == EntityState.Added)
                ? ValueTask.FromException<InterceptionResult<int>>(new InvalidOperationException("PRIVATE synthetic audit failure"))
                : ValueTask.FromResult(result);
    }
    private sealed class CancelAfterSave(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default)
        { cancellation.Cancel(); return ValueTask.FromResult(result); }
    }
    private sealed class CommitFailure : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(System.Data.Common.DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken ct = default) =>
            ValueTask.FromException<InterceptionResult>(new TimeoutException("PRIVATE synthetic ambiguous commit"));
    }
}
