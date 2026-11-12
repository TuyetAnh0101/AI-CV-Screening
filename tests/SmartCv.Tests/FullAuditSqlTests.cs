using System.Diagnostics;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

/// <summary>Opt-in SQL regression with fresh retained databases. Never drops tables/databases or deletes fixture data.</summary>
public class FullAuditSqlTests
{
    [RuntimeSqlFact]
    public async Task Concurrent_HR_saves_have_one_winner_and_read_endpoints_remain_consistent()
    {
        var gate = new ConcurrentSaveGate();
        await using var fixture = await RetainedDatabase.CreateAsync(gate);
        gate.Armed = true;
        var requests = new[] { "Reviewing", "Shortlisted" }.Select(stage => fixture.Client.PatchAsJsonAsync(
            "/api/applications/1/stage", new ChangeStageRequest { ToStage = stage, Actor = "Synthetic HR" })).ToArray();
        var responses = await Task.WhenAll(requests);
        try
        {
            Assert.Equal(2, gate.Arrivals);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        }
        finally { foreach (var response in responses) response.Dispose(); }
        gate.Armed = false;
        await using var db = fixture.Context();
        Assert.Equal(1, await db.ApplicationEvents.CountAsync());
        var saved = await db.Applications.AsNoTracking().SingleAsync(a => a.ApplicationId == 1);
        Assert.Contains(saved.Stage, new[] { "Reviewing", "Shortlisted" });
        var unchanged = await fixture.Client.PatchAsJsonAsync("/api/applications/1/stage", new ChangeStageRequest
        { ToStage = saved.Stage, Actor = "Synthetic HR", ExpectedStageVersion = saved.StageVersion });
        unchanged.EnsureSuccessStatusCode();
        Assert.Equal("Unchanged", (await unchanged.Content.ReadFromJsonAsync<StageChangeResultDto>())!.Outcome);
        Assert.Equal(1, await db.ApplicationEvents.CountAsync());

        foreach (var reason in new[] { "MissingSkills", "NotEnoughExperience" })
        {
            using var reject = await fixture.Client.PatchAsJsonAsync("/api/applications/1/stage", new ChangeStageRequest
            { ToStage = "Rejected", RejectReason = reason, Actor = "Synthetic HR" });
            reject.EnsureSuccessStatusCode();
        }
        using var reopen = await fixture.Client.PatchAsJsonAsync("/api/applications/1/stage", new ChangeStageRequest
        { ToStage = "Interview", Actor = "Synthetic HR" });
        reopen.EnsureSuccessStatusCode();
        var events = (await fixture.Client.GetFromJsonAsync<List<ApplicationEventDto>>("/api/applications/1/events"))!;
        Assert.Equal(4, events.Count);
        Assert.All(events, e => Assert.Equal("Synthetic HR", e.Actor));
        Assert.Equal("Interview", events[0].ToStage);
        Assert.Null(events[0].RejectReason);
        var clock = Stopwatch.StartNew();
        var reads = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => fixture.Client.GetFromJsonAsync<RankingResponse>("/api/runs/25/ranking")));
        Assert.All(reads, ranking =>
        {
            Assert.Equal(1, ranking!.StageCounts["Interview"]);
            Assert.Equal(1, ranking.StageCounts["New"]);
            Assert.Equal(2, ranking.Items.Count);
            Assert.All(ranking.Items, item => Assert.Equal(80, item.FinalScore));
        });
        var detail = (await fixture.Client.GetFromJsonAsync<EvaluationDetailDto>("/api/evaluations/1"))!;
        Assert.Equal("Interview", detail.Stage);
        Assert.Equal(80, detail.FinalScore);
        var details = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => fixture.Client.GetFromJsonAsync<EvaluationDetailDto>("/api/evaluations/1")));
        Assert.All(details, item => { Assert.Equal("Interview", item!.Stage); Assert.Equal(80, item.FinalScore); });
        Console.WriteLine($"32 concurrent ranking reads completed in {clock.ElapsedMilliseconds} ms on isolated SQL.");
        await File.WriteAllTextAsync(TestSupport.RepoFile("artifacts/full-audit-tests/concurrent-sql-evidence.txt"),
            $"Database={fixture.Name}\nConcurrent saves=2, Changed=1, Conflict=1, history after race=1\nFinal history=4; 32 parallel ranking reads=200; 32 parallel evaluation reads=200; AI scores unchanged\nElapsedMilliseconds={clock.ElapsedMilliseconds}\n");
    }

    [RuntimeSqlFact]
    public async Task SQL_failure_during_bulk_rolls_back_all_decisions_and_history()
    {
        var failure = new FailAfterFirstWrite();
        await using var fixture = await RetainedDatabase.CreateAsync(failure);
        await using var db = fixture.Context();
        var before = await db.Applications.AsNoTracking().OrderBy(a => a.ApplicationId)
            .Select(a => new { a.Stage, a.RejectReason, a.StageUpdatedAt, a.StageVersion }).ToListAsync();
        failure.Armed = true;
        using var response = await fixture.Client.PostAsJsonAsync("/api/applications/stage", new BulkChangeStageRequest
        { ApplicationIds = [1, 2], ToStage = "Interview", Actor = "Synthetic HR" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("51050", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, failure.CompletedWrites);
        Assert.True(failure.Injected);
        var after = await db.Applications.AsNoTracking().OrderBy(a => a.ApplicationId)
            .Select(a => new { a.Stage, a.RejectReason, a.StageUpdatedAt, a.StageVersion }).ToListAsync();
        Assert.Equal(before, after);
        Assert.Equal(0, await db.ApplicationEvents.CountAsync());
        using var rejected = await fixture.Client.PatchAsJsonAsync("/api/applications/1/stage", new ChangeStageRequest
        { ToStage = "Rejected", Actor = "Synthetic HR" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Client.GetAsync("/api/applications")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/api/evaluations/2147483647")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("/api/health/database")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("/api/jobs/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/api/jobs/2147483647")).StatusCode);
        Assert.Equal(6, (await fixture.Client.GetFromJsonAsync<StageOptionsResponse>("/api/applications/stage-options"))!.Stages.Count);
    }

    private sealed class ConcurrentSaveGate : SaveChangesInterceptor
    {
        public bool Armed;
        private int arrivals;
        public int Arrivals => arrivals;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<Application>().Any(e => e.State == EntityState.Modified))
            {
                if (Interlocked.Increment(ref arrivals) == 2) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    private sealed class FailAfterFirstWrite : DbCommandInterceptor
    {
        public bool Armed;
        public int CompletedWrites;
        public bool Injected;
        private static bool IsWrite(DbCommand command) => command.CommandText.Contains("UPDATE [Applications]", StringComparison.Ordinal)
            || command.CommandText.Contains("INSERT INTO [ApplicationEvents]", StringComparison.Ordinal);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && IsWrite(command) && CompletedWrites == 1)
            {
                // The first statement ran on SQL Server. Fail the second on the server to verify the enclosing rollback.
                command.CommandText = "THROW 51050, N'Synthetic full audit transaction failure', 1;";
                Injected = true;
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

    private sealed class RetainedDatabase : IAsyncDisposable
    {
        private readonly string connection;
        private WebApplicationFactory<Program>? factory;
        public string Name { get; }
        public HttpClient Client { get; private set; } = null!;
        private RetainedDatabase(SqlConnectionStringBuilder builder) { Name = builder.InitialCatalog; connection = builder.ConnectionString; }
        public SmartCvDbContext Context() => new(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options);
        public static async Task<RetainedDatabase> CreateAsync(IInterceptor? interceptor = null)
        {
            var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(RuntimeSqlFactAttribute.Setting));
            RuntimeSqlFactAttribute.Validate(builder);
            builder.InitialCatalog = "SmartCv_Runtime_Test_" + Guid.NewGuid().ToString("N");
            builder.Pooling = false; builder.ConnectTimeout = 10;
            var fixture = new RetainedDatabase(builder);
            Directory.CreateDirectory(TestSupport.RepoFile("artifacts/full-audit-tests"));
            await File.AppendAllTextAsync(TestSupport.RepoFile("artifacts/full-audit-tests/retained-sql-databases.txt"), fixture.Name + Environment.NewLine);
            await using (var db = fixture.Context())
            {
                await db.Database.MigrateAsync();
                await db.Database.ExecuteSqlRawAsync("""
                    DECLARE @a uniqueidentifier=NEWID(),@b uniqueidentifier=NEWID();
                    INSERT INTO Cvs(CvId,OriginalFileName,StoredPath,ContentType,FileSizeBytes,FileHash,ExtractionStatus,ExtractedText)
                    VALUES(@a,N'synthetic-a.pdf',N'test',N'application/pdf',1,REPLICATE('A',64),N'Completed',N'C# SQL'),
                          (@b,N'synthetic-b.pdf',N'test',N'application/pdf',1,REPLICATE('B',64),N'Completed',N'C# SQL');
                    INSERT INTO Applications(CvId,JobId) VALUES(@a,1),(@b,1);
                    INSERT INTO PromptVersions(VersionName,SystemPrompt,IsActive) VALUES(N'Synthetic audit prompt',N'Synthetic audit scoring',1);
                    SET IDENTITY_INSERT EvaluationRuns ON;
                    INSERT INTO EvaluationRuns(RunId,RunType,PromptVersionId,ModelName,Temperature,KeywordWeight,AiWeight,Status,TotalItems,ProcessedItems)
                    VALUES(25,N'Screening',1,N'synthetic',0.2,0.3,0.7,N'Completed',2,2);
                    SET IDENTITY_INSERT EvaluationRuns OFF;
                    INSERT INTO Evaluations(RunId,ApplicationId,FinalScore,KeywordScore,AiScore,Status,ScoreSource,PredictedPass)
                    SELECT 25,ApplicationId,80,80,80,N'Completed',N'Combined',1 FROM Applications;
                    """);
            }
            fixture.factory = TestSupport.ApiFactory(settings: new() { ["Scoring:WorkerEnabled"] = "false", ["Gemini:UseFake"] = "true" })
                .WithWebHostBuilder(host =>
                {
                    host.ConfigureLogging(logging => logging.ClearProviders());
                    host.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<DbContextOptions<SmartCvDbContext>>();
                        services.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                        services.AddDbContext<SmartCvDbContext>(options =>
                        {
                            options.UseSqlServer(fixture.connection, sql => { if (interceptor is FailAfterFirstWrite) sql.MaxBatchSize(1); });
                            if (interceptor is not null) options.AddInterceptors(interceptor);
                        });
                    });
                });
            fixture.Client = fixture.factory.CreateClient();
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            if (factory is not null) await factory.DisposeAsync();
            // Intentionally retain the unique fixture database for inspection.
        }
    }
}
