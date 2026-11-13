using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Applications;

/// <summary>
/// Opt-in SQL tests: only a generated SmartCv_A35_Test_{guid} database is ever migrated/dropped.
/// SMARTCV_A35_TEST_SQL_CONNECTION must designate a SmartCv_A35_Test_ database (not the application DB).
/// </summary>
public class ApplicationStageSqlServerTests
{
    [A35SqlFact]
    public async Task Migration_upgrades_existing_application_and_database_checks_and_trigger_protect_history()
    {
        await using var env = await SqlEnvironment.CreateAsync(upgradeExisting: true);
        await using var db = env.Context();
        var app = await db.Applications.SingleAsync();
        Assert.Equal("New", app.Stage);
        Assert.Null(app.RejectReason);
        Assert.Null(app.StageUpdatedAt);
        Assert.NotEqual(Guid.Empty, app.StageVersion);
        Assert.Equal("existing-before-A35.pdf", (await db.Cvs.SingleAsync()).OriginalFileName);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        foreach (var pair in new (string Stage, string? Reason)[]
        {
            ("Rejected", null), ("New", "Duplicate"), ("Invalid", null), ("new", null), ("Rejected", "invalid")
        })
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE Applications SET Stage={pair.Stage}, RejectReason={pair.Reason} WHERE ApplicationId={app.ApplicationId}"));
            Assert.Equal(547, error.Number);
        }
        using var changed = await env.Client.PatchAsJsonAsync($"/api/applications/{app.ApplicationId}/stage",
            ApplicationStageEndpointTests.Request("Rejected", "MissingSkills"));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var ev = await db.ApplicationEvents.AsNoTracking().SingleAsync();
        Assert.Equal("HR - Trần Thu Hà", ev.Actor);
        Assert.Equal("Đủ kỹ năng", ev.Note);
        var update = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ApplicationEvents SET Note=N'changed' WHERE EventId={ev.EventId}"));
        Assert.Equal(51000, update.Number);
        var delete = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM ApplicationEvents WHERE EventId={ev.EventId}"));
        Assert.Equal(51000, delete.Number);
        var foreignKey = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM Applications WHERE ApplicationId={app.ApplicationId}"));
        Assert.Equal(547, foreignKey.Number);
        var invalidEvent = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO ApplicationEvents(ApplicationId,FromStage,ToStage,RejectReason,Actor) VALUES({app.ApplicationId},N'New',N'Rejected',NULL,N'HR')"));
        Assert.Equal(547, invalidEvent.Number);
        var emptyActor = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO ApplicationEvents(ApplicationId,FromStage,ToStage,Actor) VALUES({app.ApplicationId},N'New',N'Reviewing',N'   ')"));
        Assert.Equal(547, emptyActor.Number);
        Assert.Single(await db.ApplicationEvents.AsNoTracking().ToListAsync());
        Assert.Equal("Đủ kỹ năng", (await db.ApplicationEvents.AsNoTracking().SingleAsync()).Note);
    }

    [A35SqlFact]
    public async Task Bulk_twenty_applications_have_one_event_each_and_can_change_reason_then_reopen()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        var ids = await env.SeedAsync(20);
        var request = new BulkChangeStageRequest { ApplicationIds = [.. ids, 999999], ToStage = "Rejected", RejectReason = "MissingSkills", Actor = "HR" };
        using var first = await env.Client.PostAsJsonAsync("/api/applications/stage", request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var result = (await first.Content.ReadFromJsonAsync<BulkStageChangeResponse>())!;
        Assert.Equal((21, 20, 0, 1), (result.Total, result.Changed, result.Unchanged, result.NotFound));
        Assert.Equal(20, result.Results.Where(r => r.EventId is not null).Select(r => r.EventId).Distinct().Count());
        using var repeated = await env.Client.PostAsJsonAsync("/api/applications/stage", request);
        var noop = (await repeated.Content.ReadFromJsonAsync<BulkStageChangeResponse>())!;
        Assert.Equal((0, 20, 1), (noop.Changed, noop.Unchanged, noop.NotFound));
        var reason = ApplicationStageEndpointTests.Request("Rejected", "Duplicate");
        reason.ExpectedStageVersion = result.Results[0].StageVersion;
        Assert.Equal(HttpStatusCode.OK, (await env.Client.PatchAsJsonAsync($"/api/applications/{ids[0]}/stage", reason)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await env.Client.PatchAsJsonAsync($"/api/applications/{ids[0]}/stage", reason)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await env.Client.PatchAsJsonAsync($"/api/applications/{ids[0]}/stage", ApplicationStageEndpointTests.Request("Reviewing"))).StatusCode);
        await using var db = env.Context();
        Assert.Equal(22, await db.ApplicationEvents.CountAsync());
        var reopened = await db.Applications.FindAsync(ids[0]);
        Assert.Equal("Reviewing", reopened!.Stage);
        Assert.Null(reopened.RejectReason);
        var history = (await env.Client.GetFromJsonAsync<List<ApplicationEventDto>>($"/api/applications/{ids[0]}/events"))!;
        Assert.Equal(new[] { "Reviewing", "Rejected", "Rejected" }, history.Select(e => e.ToStage));
        Assert.Equal(new string?[] { null, "Duplicate", "MissingSkills" }, history.Select(e => e.RejectReason));
    }

    [A35SqlFact]
    public async Task Ranking_and_detail_return_current_HR_stage_without_changing_AI_scores()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        var ids = await env.SeedAsync(2);
        int runId;
        await using (var db = env.Context())
        {
            var run = new EvaluationRun
            {
                RunType = "Screening", Status = "Completed", TotalItems = 2, ProcessedItems = 2,
                PromptVersion = new PromptVersion { VersionName = "A35 SQL results", SystemPrompt = "Synthetic" },
                ModelName = "synthetic", Temperature = .2m, KeywordWeight = .3m, AiWeight = .7m,
                PassThreshold = 70m, CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
                Evaluations = ids.Select(id => new Evaluation
                {
                    ApplicationId = id, Status = "Completed", KeywordScore = 80m, AiScore = 80m,
                    FinalScore = 80m, FitLabel = "Good", ScoreSource = "Combined", PredictedPass = true, CreatedAt = DateTime.UtcNow
                }).ToList()
            };
            db.Add(run);
            await db.SaveChangesAsync();
            runId = run.RunId;
        }
        var before = (await env.Client.GetFromJsonAsync<RankingResponse>($"/api/runs/{runId}/ranking"))!;
        Assert.Equal(2, before.StageCounts["New"]); // AI evaluations never decide the application stage.
        Assert.All(before.Items, i => Assert.Equal("New", i.Stage));
        Assert.Equal(HttpStatusCode.OK, (await env.Client.PatchAsJsonAsync($"/api/applications/{ids[0]}/stage",
            ApplicationStageEndpointTests.Request("OnHold"))).StatusCode);
        var ranking = (await env.Client.GetFromJsonAsync<RankingResponse>($"/api/runs/{runId}/ranking"))!;
        Assert.Equal(6, ranking.StageCounts.Count);
        Assert.Equal(1, ranking.StageCounts["New"]);
        Assert.Equal(1, ranking.StageCounts["OnHold"]);
        foreach (var item in ranking.Items)
        {
            var detail = (await env.Client.GetFromJsonAsync<EvaluationDetailDto>($"/api/evaluations/{item.EvaluationId}"))!;
            Assert.Equal((item.Stage, item.RejectReason, item.StageUpdatedAt, item.StageVersion),
                (detail.Stage, detail.RejectReason, detail.StageUpdatedAt, detail.StageVersion));
            Assert.Equal(80m, detail.FinalScore);
            if (item.Stage == "OnHold") Assert.Equal(DateTimeKind.Utc, detail.StageUpdatedAt!.Value.Kind);
        }
    }

    [A35SqlFact]
    public async Task Concurrent_HR_requests_without_expected_stage_have_one_winner_and_one_409_at_save()
    {
        var barrier = new DecisionBarrier();
        await using var env = await SqlEnvironment.CreateAsync(barrier);
        var id = (await env.SeedAsync(1))[0];
        barrier.Armed = true;
        var responses = await Task.WhenAll(
            env.Client.PatchAsJsonAsync($"/api/applications/{id}/stage", ApplicationStageEndpointTests.Request("Shortlisted")),
            env.Client.PatchAsJsonAsync($"/api/applications/{id}/stage", ApplicationStageEndpointTests.Request("Interview")));
        try
        {
            Assert.Equal(2, barrier.Arrivals);
            Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, responses.Select(r => r.StatusCode).Order());
            await using var db = env.Context();
            var ev = await db.ApplicationEvents.SingleAsync();
            var app = await db.Applications.SingleAsync();
            Assert.Equal(ev.ToStage, app.Stage);
            var winner = (await responses.Single(r => r.IsSuccessStatusCode).Content.ReadFromJsonAsync<StageChangeResultDto>())!;
            Assert.Equal(ev.EventId, winner.EventId);
            Assert.Equal(app.StageVersion, winner.StageVersion);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [A35SqlFact]
    public async Task Concurrent_reason_changes_at_same_stage_are_not_silently_overwritten()
    {
        var barrier = new DecisionBarrier();
        await using var env = await SqlEnvironment.CreateAsync(barrier);
        var id = (await env.SeedAsync(1))[0];
        Assert.Equal(HttpStatusCode.OK, (await env.Client.PatchAsJsonAsync($"/api/applications/{id}/stage",
            ApplicationStageEndpointTests.Request("Rejected", "MissingSkills"))).StatusCode);
        barrier.Armed = true;
        var responses = await Task.WhenAll(
            env.Client.PatchAsJsonAsync($"/api/applications/{id}/stage", ApplicationStageEndpointTests.Request("Rejected", "Duplicate")),
            env.Client.PatchAsJsonAsync($"/api/applications/{id}/stage", ApplicationStageEndpointTests.Request("Rejected", "PositionFilled")));
        try
        {
            Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, responses.Select(r => r.StatusCode).Order());
            await using var db = env.Context();
            Assert.Equal(2, await db.ApplicationEvents.CountAsync());
            var last = await db.ApplicationEvents.OrderByDescending(e => e.EventId).FirstAsync();
            Assert.Equal(last.RejectReason, (await db.Applications.SingleAsync()).RejectReason);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [A35SqlFact]
    public async Task Concurrent_bulk_and_single_decisions_roll_back_entire_losing_bulk()
    {
        var barrier = new DecisionBarrier();
        await using var env = await SqlEnvironment.CreateAsync(barrier);
        var ids = await env.SeedAsync(2);
        barrier.Armed = true;
        var responses = await Task.WhenAll(
            env.Client.PostAsJsonAsync("/api/applications/stage", new BulkChangeStageRequest { ApplicationIds = ids, ToStage = "Interview", Actor = "HR bulk" }),
            env.Client.PatchAsJsonAsync($"/api/applications/{ids[0]}/stage", ApplicationStageEndpointTests.Request("OnHold")));
        try
        {
            Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, responses.Select(r => r.StatusCode).Order());
            await using var db = env.Context();
            var apps = await db.Applications.OrderBy(a => a.ApplicationId).ToListAsync();
            if (responses[0].IsSuccessStatusCode)
            {
                Assert.All(apps, a => Assert.Equal("Interview", a.Stage));
                Assert.Equal(2, await db.ApplicationEvents.CountAsync());
            }
            else
            {
                Assert.Equal("OnHold", apps[0].Stage);
                Assert.Equal("New", apps[1].Stage);
                Assert.Single(await db.ApplicationEvents.ToListAsync());
                using var json = JsonDocument.Parse(await responses[0].Content.ReadAsStringAsync());
                Assert.True(json.RootElement.GetProperty("rolledBack").GetBoolean());
                Assert.Equal(2, json.RootElement.GetProperty("results").GetArrayLength());
            }
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [A35SqlFact]
    public async Task Failure_after_first_database_write_rolls_back_applications_and_events()
    {
        var fault = new AfterFirstWriteFailure();
        await using var env = await SqlEnvironment.CreateAsync(fault);
        var ids = await env.SeedAsync(2);
        fault.Armed = true;
        using var response = await env.Client.PostAsJsonAsync("/api/applications/stage",
            new BulkChangeStageRequest { ApplicationIds = ids, ToStage = "Shortlisted", Actor = "HR" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, fault.CompletedWrites);
        Assert.True(fault.Failed);
        await using var db = env.Context();
        Assert.Empty(await db.ApplicationEvents.ToListAsync());
        Assert.All(await db.Applications.ToListAsync(), a => { Assert.Equal("New", a.Stage); Assert.Null(a.StageUpdatedAt); });
    }

    private sealed class DecisionBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public bool Armed { get; set; }
        public int Arrivals => arrivals;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            // Both HR requests must read the old version before either starts writing.
            // A38 writes/locks the shared CV before SaveChanges; waiting there would manufacture a timeout.
            if (Armed && Volatile.Read(ref arrivals) < 2
                && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("FROM [Applications]"))
            {
                if (Interlocked.Increment(ref arrivals) == 2) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    private sealed class AfterFirstWriteFailure : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public int CompletedWrites { get; private set; }
        public bool Failed { get; private set; }
        private static bool IsWrite(DbCommand command) => command.CommandText.Contains("UPDATE [Applications]")
            || command.CommandText.Contains("INSERT INTO [ApplicationEvents]");
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && IsWrite(command) && CompletedWrites == 1)
            {
                Failed = true;
                throw new InvalidOperationException("Synthetic A35 failure after first successful write, before second write.");
            }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Armed && IsWrite(command)) CompletedWrites++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SqlEnvironment : IAsyncDisposable
    {
        private readonly string connection;
        private WebApplicationFactory<Program>? factory;
        public HttpClient Client { get; private set; } = null!;
        private SqlEnvironment(string connection) => this.connection = connection;

        public static async Task<SqlEnvironment> CreateAsync(IInterceptor? interceptor = null, bool upgradeExisting = false)
        {
            var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A35SqlFactAttribute.Setting));
            A35SqlFactAttribute.Validate(builder);
            builder.InitialCatalog = "SmartCv_A35_Test_" + Guid.NewGuid().ToString("N");
            builder.Pooling = false;
            builder.ConnectTimeout = 10;
            var env = new SqlEnvironment(builder.ConnectionString);
            try
            {
                await using (var db = env.Context())
                {
                    if (upgradeExisting)
                    {
                        await db.GetService<IMigrator>().MigrateAsync("20261106142155_SeedJobs");
                        var cv = Guid.NewGuid();
                        var hash = cv.ToString("N").PadRight(64, '0');
                        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Cvs(CvId,OriginalFileName,StoredPath,ContentType,FileSizeBytes,FileHash) VALUES({cv},N'existing-before-A35.pdf',N'test',N'application/pdf',1,{hash})");
                        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Applications(CvId,JobId) VALUES({cv},1)");
                    }
                    await db.Database.MigrateAsync();
                }
                env.factory = TestSupport.ApiFactory(settings: new() { ["Scoring:WorkerEnabled"] = "false" })
                    .WithWebHostBuilder(host =>
                    {
                        host.ConfigureLogging(logging => logging.ClearProviders());
                        host.ConfigureTestServices(services =>
                        {
                            services.RemoveAll<DbContextOptions<SmartCvDbContext>>();
                            services.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                            services.AddDbContext<SmartCvDbContext>(options =>
                            {
                                // Use production batching except when injecting a failure between writes.
                                options.UseSqlServer(env.connection, sql =>
                                {
                                    if (interceptor is AfterFirstWriteFailure) sql.MaxBatchSize(1);
                                });
                                if (interceptor is not null) options.AddInterceptors(interceptor);
                            });
                        });
                    });
                env.Client = env.factory.CreateClient();
                return env;
            }
            catch { await env.DisposeAsync(); throw; }
        }

        public SmartCvDbContext Context() => new(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options);
        public async Task<List<int>> SeedAsync(int count)
        {
            await using var db = Context();
            var apps = Enumerable.Range(1, count).Select(_ => ApplicationStageEndpointTests.NewApplication()).ToList();
            db.AddRange(apps);
            await db.SaveChangesAsync();
            return apps.Select(a => a.ApplicationId).ToList();
        }
        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            if (factory is not null) await factory.DisposeAsync();
            var builder = new SqlConnectionStringBuilder(connection);
            A35SqlFactAttribute.Validate(builder);
            if (!Guid.TryParseExact(builder.InitialCatalog["SmartCv_A35_Test_".Length..], "N", out _))
                throw new InvalidOperationException("Refusing cleanup outside the generated A35 test database.");
            await using var db = Context();
            await db.Database.EnsureDeletedAsync();
        }
    }
}

public sealed class A35SqlFactAttribute : FactAttribute
{
    public const string Setting = "SMARTCV_A35_TEST_SQL_CONNECTION";
    public A35SqlFactAttribute()
    {
        var setting = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrWhiteSpace(setting)) { Skip = $"Set {Setting} to run isolated SQL Server A35 tests."; return; }
        try { Validate(new SqlConnectionStringBuilder(setting)); }
        catch (ArgumentException) { Skip = "Connection must designate SmartCv_A35_Test_ without AttachDBFilename."; }
    }
    internal static void Validate(SqlConnectionStringBuilder builder)
    {
        if (!builder.InitialCatalog.StartsWith("SmartCv_A35_Test_", StringComparison.Ordinal) || !string.IsNullOrEmpty(builder.AttachDBFilename))
            throw new ArgumentException("Only explicitly designated A35 test databases are allowed.");
    }
}
