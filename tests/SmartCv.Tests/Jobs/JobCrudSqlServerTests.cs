using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Jobs;

public class JobCrudSqlServerTests
{
    [A21SqlFact]
    public async Task Migration_Up_preserves_A20_index_all_seed_business_data_and_foreign_keys()
    {
        await using var env = await A21SqlEnvironment.CreateAsync(current: false);
        await env.SeedBusinessAsync();
        var before = await env.SnapshotAsync("before-up");
        await env.MigrateAsync(JobCrudMigrationTests.Current);
        await env.AssertCurrentSchemaAsync();
        Assert.Equal(5, await env.IntAsync("SELECT COUNT(*) FROM dbo.Jobs WHERE Headcount IS NULL AND ClosingDate IS NULL;"));
        Assert.Equal(5, before.Counts["Jobs"]); Assert.Equal(29, before.Counts["JobSkills"]); Assert.Equal(52, before.Counts["JobSkillAliases"]);
        Assert.Equal(2, before.Counts["Applications"]); Assert.Equal(2, before.Counts["Cvs"]);
        AssertSnapshot(before, await env.SnapshotAsync("after-up"));
        await using var connection = await env.OpenAsync();
        foreach (var headcount in new[] { 0, -1, 1001 })
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            await using var command = new SqlCommand("UPDATE dbo.Jobs SET Headcount=@value WHERE JobId=1;", connection, transaction);
            command.Parameters.AddWithValue("@value", headcount);
            var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync()); Assert.Equal(547, error.Number);
            await transaction.RollbackAsync();
        }
        await env.WriteAsync("up-result", new { NewColumnsNullable = true, HeadcountCheck = true, A20UniqueIndexPreserved = true, LegacyDataPreserved = true, History = await env.HistoryAsync() });
    }

    [A21SqlFact]
    public async Task Exported_Up_and_Down_on_an_independent_catalog_preserve_legacy_rows_and_history()
    {
        await using var env = await A21SqlEnvironment.CreateAsync(current: false);
        await env.SeedBusinessAsync();
        var before = await env.SnapshotAsync("script-before");
        await using var db = env.Context(); var migrator = db.GetService<IMigrator>();
        var sql = await File.ReadAllTextAsync(TestSupport.RepoFile("database/a21_job_headcount_closing_date.sql"));
        Assert.Equal(Normalize(Guard(migrator.GenerateScript(JobCrudMigrationTests.Previous, JobCrudMigrationTests.Current))), Normalize(sql));
        await env.ScriptAsync(sql);
        await env.AssertCurrentSchemaAsync();
        AssertSnapshot(before, await env.SnapshotAsync("script-after-up"));
        await env.ScriptAsync("UPDATE dbo.Jobs SET Headcount=1000,ClosingDate='2026-12-31' WHERE JobId=1;");
        var rollback = Guard(migrator.GenerateScript(JobCrudMigrationTests.Current, JobCrudMigrationTests.Previous));
        await env.WriteTextAsync("rollback.sql", rollback);
        await env.ScriptAsync(rollback);
        Assert.Equal(0, await env.IntAsync("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.Jobs') AND name IN (N'Headcount',N'ClosingDate');"));
        Assert.Equal(0, await env.IntAsync("SELECT COUNT(*) FROM sys.check_constraints WHERE name=N'CK_Jobs_Headcount';"));
        Assert.Equal(1, await env.IntAsync("SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Jobs') AND name=N'UQ_Jobs_Title' AND is_unique=1 AND is_unique_constraint=0;"));
        Assert.Equal(JobCrudMigrationTests.Previous, (await env.HistoryAsync())[^1]);
        AssertSnapshot(before, await env.SnapshotAsync("script-after-down"));
    }

    [A21SqlFact]
    public async Task Real_SQL_HTTP_create_edit_toggle_assign_and_legacy_scores_preserve_contracts()
    {
        await using var env = await A21SqlEnvironment.CreateAsync();
        var cvId = await env.SeedBusinessAsync();
        using var factory = env.Api(kestrel: true); using var client = factory.CreateClient();
        var server = factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        Assert.Contains("Kestrel", server.GetType().FullName!);
        client.BaseAddress = new Uri(server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.Single());
        var request = JobRulesTests.Valid("  Data\tAnalyst A21  ");
        using var created = await client.PostAsJsonAsync("/api/jobs", request); Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var job = (await created.Content.ReadFromJsonAsync<JobDto>())!;
        Assert.Equal("Data Analyst A21", job.Title); Assert.Equal($"/api/jobs/{job.JobId}", created.Headers.Location!.OriginalString);
        Assert.Equal(0, job.ApplicationCount); Assert.False(job.HasEvaluations); Assert.Equal(6, job.StageCounts!.Count);
        var caseInsensitive = await env.IntAsync("SELECT CASE WHEN N'Data Analyst A21'=N'data analyst a21' THEN 1 ELSE 0 END;") == 1;
        using var duplicate = await client.PostAsJsonAsync("/api/jobs", JobRulesTests.Valid(caseInsensitive ? "data analyst a21" : job.Title));
        await JobCrudEndpointTests.AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "title");
        request.Title = "Data Analyst A21 renamed"; request.Headcount = 1000; request.ClosingDate = new DateOnly(2020, 1, 1);
        using var edit = await client.PutAsJsonAsync($"/api/jobs/{job.JobId}", request); Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.True((await edit.Content.ReadFromJsonAsync<JobDto>())!.IsExpired);
        for (var i = 0; i < 2; i++)
        {
            using var stop = await client.PostAsync($"/api/jobs/{job.JobId}/deactivate", null); Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        }
        Assert.DoesNotContain((await client.GetFromJsonAsync<List<JobDto>>("/api/jobs"))!, j => j.JobId == job.JobId);
        Assert.Contains((await client.GetFromJsonAsync<List<JobDto>>("/api/jobs?activeOnly=false"))!, j => j.JobId == job.JobId && !j.IsActive);
        var assignment = new CreateApplicationsRequest { JobId = job.JobId, CvIds = [cvId] };
        using var refused = await client.PostAsJsonAsync("/api/applications", assignment);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        for (var i = 0; i < 2; i++)
        {
            using var start = await client.PostAsync($"/api/jobs/{job.JobId}/activate", null); Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        }
        using var assigned = await client.PostAsJsonAsync("/api/applications", assignment); Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        Assert.Equal(1, (await assigned.Content.ReadFromJsonAsync<CreateApplicationsResponse>())!.CreatedCount);
        Assert.Equal(1, (await client.GetFromJsonAsync<JobDto>($"/api/jobs/{job.JobId}"))!.ApplicationCount);
        var existing = (await client.GetFromJsonAsync<JobDto>("/api/jobs/1"))!; Assert.True(existing.HasEvaluations);
        var oldSkills = JsonSerializer.Serialize(existing.Skills);
        var criteria = JobRulesTests.Valid(existing.Title); criteria.MinGpa = 4; criteria.MinYearsExperience = 50;
        using var changedCriteria = await client.PutAsJsonAsync("/api/jobs/1", criteria); Assert.Equal(HttpStatusCode.OK, changedCriteria.StatusCode);
        var changed = (await changedCriteria.Content.ReadFromJsonAsync<JobDto>())!;
        Assert.True(changed.HasEvaluations); Assert.Equal(oldSkills, JsonSerializer.Serialize(changed.Skills));
        Assert.Equal(2, await env.IntAsync("SELECT COUNT(*) FROM dbo.Evaluations WHERE FinalScore=77;"));
        Assert.Equal(2, await env.IntAsync("SELECT COUNT(*) FROM dbo.ApplicationEvents;"));
        Assert.Equal(2, await env.IntAsync("SELECT COUNT(*) FROM dbo.Cvs;"));
        using var missing = await client.PutAsJsonAsync("/api/jobs/2147483647", criteria); await JobCrudEndpointTests.AssertProblemAsync(missing, HttpStatusCode.NotFound);
        using var deletion = await client.DeleteAsync($"/api/jobs/{job.JobId}"); Assert.Equal(HttpStatusCode.MethodNotAllowed, deletion.StatusCode);
        await env.WriteAsync("http-result", new { Create = 201, Duplicate = 409, Rename = 200, RepeatedToggle = 200, InactiveAssignment = 400, ActiveAssignment = 200, SavedScoresUnchanged = true, GeminiCalled = false });
    }

    [A21SqlFact]
    public async Task Concurrent_create_and_rename_reach_unique_index_and_return_one_success_one_409()
    {
        var barrier = new JobSaveBarrier();
        await using var env = await A21SqlEnvironment.CreateAsync(); using var factory = env.Api(barrier); using var client = factory.CreateClient();
        barrier.Arm("Race A21");
        var responses = await Task.WhenAll(client.PostAsJsonAsync("/api/jobs", JobRulesTests.Valid(" Race  A21 ")), client.PostAsJsonAsync("/api/jobs", JobRulesTests.Valid("Race A21")));
        Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, responses.Select(r => r.StatusCode).Order());
        Assert.Equal(2, barrier.Arrivals); Assert.Equal(1, barrier.UniqueFailures);
        foreach (var response in responses) response.Dispose();
        barrier.Disarm();
        using var one = await client.PostAsJsonAsync("/api/jobs", JobRulesTests.Valid("Rename one A21"));
        using var two = await client.PostAsJsonAsync("/api/jobs", JobRulesTests.Valid("Rename two A21"));
        var a = (await one.Content.ReadFromJsonAsync<JobDto>())!; var b = (await two.Content.ReadFromJsonAsync<JobDto>())!;
        barrier.Arm("Shared rename A21");
        responses = await Task.WhenAll(client.PutAsJsonAsync($"/api/jobs/{a.JobId}", JobRulesTests.Valid("Shared rename A21")), client.PutAsJsonAsync($"/api/jobs/{b.JobId}", JobRulesTests.Valid("Shared rename A21")));
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, responses.Select(r => r.StatusCode).Order());
        Assert.Equal(2, barrier.Arrivals); Assert.Equal(1, barrier.UniqueFailures);
        foreach (var response in responses) response.Dispose();
        Assert.Equal(1, await env.IntAsync("SELECT COUNT(*) FROM dbo.Jobs WHERE Title=N'Shared rename A21';"));
        await env.WriteAsync("race-result", new { CreateStatuses = new[] { 201, 409 }, RenameStatuses = new[] { 200, 409 }, RequestsPastPrecheck = 2, UniqueSqlFailuresPerRace = 1 });
    }

    [A21SqlFact]
    public async Task Stats_match_SQL_for_many_jobs_are_batched_private_and_cancellable()
    {
        var recorder = new JobReadRecorder();
        await using var env = await A21SqlEnvironment.CreateAsync();
        await using (var db = env.Context())
        {
            var run = new EvaluationRun { PromptVersion = new PromptVersion { VersionName = "A21 stats", SystemPrompt = "SECRET-A21" }, RunType = RunTypes.Screening, ModelName = "synthetic", Status = RunStatuses.Completed, KeywordWeight = .3m, AiWeight = .7m, CreatedAt = DateTime.UtcNow };
            for (var i = 0; i < 50; i++)
            {
                var job = new Job { Title = $"A21 stats {i}", IsActive = i != 49, CreatedAt = DateTime.UtcNow, ClosingDate = new DateOnly(2026, 11, 16 + i % 3) };
                job.Skills.Add(new JobSkill { SkillCode = "SQL", DisplayName = "SQL", SkillType = SkillTypes.MustHave });
                foreach (var stage in ApplicationStages.All)
                {
                    var cv = JobCrudEndpointTests.Cv(); cv.ExtractedText = "SECRET-A21"; if (i == 0) { cv.AnonymizedAt = DateTime.UtcNow; cv.ExtractedText = null; }
                    var app = new Application { Job = job, Cv = cv, Stage = stage, RejectReason = stage == ApplicationStages.Rejected ? RejectReasons.Duplicate : null };
                    db.Add(app);
                    if (stage == ApplicationStages.New) db.Add(new Evaluation { Run = run, Application = app, Status = EvaluationStatuses.Completed });
                }
            }
            await db.SaveChangesAsync();
        }
        using var factory = env.Api(recorder); using var client = factory.CreateClient(); recorder.Commands.Clear();
        var timer = Stopwatch.StartNew(); using var response = await client.GetAsync("/api/jobs?activeOnly=false"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(); var milliseconds = timer.ElapsedMilliseconds;
        var jobs = JsonSerializer.Deserialize<List<JobDto>>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(55, jobs.Count); Assert.InRange(recorder.Commands.Count, 1, 5);
        foreach (var job in jobs.Where(j => j.Title.StartsWith("A21 stats", StringComparison.Ordinal)))
        {
            Assert.Equal(6, job.ApplicationCount); Assert.True(job.HasEvaluations); Assert.Equal(1, job.MustHaveCount);
            Assert.Equal(6, job.StageCounts!.Count); Assert.All(job.StageCounts.Values, count => Assert.Equal(1, count));
            Assert.Equal(6, await env.IntAsync($"SELECT COUNT(*) FROM dbo.Applications WHERE JobId={job.JobId};")); // IDs from test API, scalar SQL cross-check.
            Assert.Equal(DateTimeKind.Utc, job.CreatedAt.Kind);
        }
        Assert.DoesNotContain("SECRET-A21", raw); Assert.DoesNotContain("candidateName", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("extractedText", raw, StringComparison.OrdinalIgnoreCase);
        Assert.All(recorder.Commands, sql => Assert.StartsWith("SELECT", sql.TrimStart(), StringComparison.OrdinalIgnoreCase));
        var queryCount = recorder.Commands.Count;
        using var publicFactory = env.Api(environment: "Production", enabled: false); using var publicClient = publicFactory.CreateClient();
        var publicBody = await publicClient.GetStringAsync("/api/jobs"); Assert.DoesNotContain("applicationCount", publicBody); Assert.DoesNotContain("hasEvaluations", publicBody); Assert.DoesNotContain("stageCounts", publicBody);
        recorder.Pause = true;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("/api/jobs", cancel.Token));
        Assert.True(recorder.CancellationObserved); recorder.Pause = false;
        await env.WriteAsync("stats-result", new { Jobs = 55, Applications = 300, QueryCount = queryCount, Milliseconds = milliseconds, SqlMatched = true, PublicStatsOmitted = true, CancellationObserved = true });
    }

    private static void AssertSnapshot(JobLegacySnapshot expected, JobLegacySnapshot actual)
    {
        Assert.Equal(expected.DataHash, actual.DataHash); Assert.Equal(expected.SchemaHash, actual.SchemaHash);
        Assert.Equal(expected.Counts.OrderBy(p => p.Key), actual.Counts.OrderBy(p => p.Key));
    }
    private static string Normalize(string sql) => sql.TrimStart('\uFEFF').Replace("\r\n", "\n").Trim();
    internal static string Guard(string generated)
    {
        const string check = "ALTER TABLE [Jobs] ADD CONSTRAINT [CK_Jobs_Headcount] CHECK (Headcount IS NULL OR Headcount BETWEEN 1 AND 1000);";
        var body = Regex.Replace(generated.Trim(), @"(?im)^GO\s*$", "").Trim().Replace(check,
            "-- Defer constraint binding until the preceding ADD COLUMN statements have completed.\nEXEC(N'" + check + "');", StringComparison.Ordinal);
        return "SET XACT_ABORT ON;\nBEGIN TRY\n" + body + "\nEND TRY\nBEGIN CATCH\n    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;\n    THROW;\nEND CATCH;\nGO";
    }
}

internal sealed class JobSaveBarrier : SaveChangesInterceptor
{
    private TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? title;
    private int arrivals;
    private int failures;
    internal int Arrivals => arrivals;
    internal int UniqueFailures => failures;
    internal void Arm(string value) { title = value; arrivals = 0; failures = 0; ready = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    internal void Disarm() => title = null;
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (title is not null && eventData.Context!.ChangeTracker.Entries<Job>().Any(e => (e.State is EntityState.Added or EntityState.Modified) && e.Entity.Title == title))
        {
            if (Interlocked.Increment(ref arrivals) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
        return result;
    }
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Exception is DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } }) Interlocked.Increment(ref failures);
        return Task.CompletedTask;
    }
}

internal sealed class JobReadRecorder : DbCommandInterceptor
{
    internal ConcurrentQueue<string> Commands { get; } = new();
    internal bool Pause { get; set; }
    internal bool CancellationObserved { get; private set; }
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Commands.Enqueue(command.CommandText);
        if (Pause)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken); }
            catch (OperationCanceledException) { CancellationObserved = cancellationToken.IsCancellationRequested; throw; }
        }
        return result;
    }
}
