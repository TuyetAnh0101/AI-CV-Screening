using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
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
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

/// <summary>Creates a new test-only database and deliberately leaves it intact; never drops/resets any database.</summary>
public class RuntimeSchemaSqlTests
{
    [RuntimeSqlFact]
    public async Task Run25_schema_mismatch_reproduces_207_and_reviewed_upgrade_preserves_data_and_HR_decisions()
    {
        var configured = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(RuntimeSqlFactAttribute.Setting));
        RuntimeSqlFactAttribute.Validate(configured);
        configured.InitialCatalog = "SmartCv_Runtime_Test_" + Guid.NewGuid().ToString("N");
        configured.Pooling = false;
        configured.ConnectTimeout = 10;
        var connection = configured.ConnectionString;
        var evidence = Path.GetDirectoryName(TestSupport.RepoFile("artifacts/runtime-fix-tests/runtime-sql-evidence.json"))!;
        Directory.CreateDirectory(evidence);
        Console.WriteLine("Created isolated test database (retained): " + configured.InitialCatalog);
        await File.AppendAllTextAsync(Path.Combine(evidence, "retained-test-databases.txt"), configured.InitialCatalog + Environment.NewLine);
        SmartCvDbContext Context() => new(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options);
        await using var db = Context();
        await db.GetService<IMigrator>().MigrateAsync("20261106142155_SeedJobs");
        await db.Database.ExecuteSqlRawAsync("""
            DECLARE @cv1 uniqueidentifier=NEWID(), @cv2 uniqueidentifier=NEWID(), @cv3 uniqueidentifier=NEWID();
            INSERT INTO Cvs(CvId,OriginalFileName,StoredPath,ContentType,FileSizeBytes,FileHash,ExtractionStatus,ExtractedText)
            VALUES (@cv1,N'synthetic-1.pdf',N'test',N'application/pdf',1,REPLICATE('1',64),N'Completed',N'C# SQL Server'),
                   (@cv2,N'synthetic-2.pdf',N'test',N'application/pdf',1,REPLICATE('2',64),N'Completed',N'C# ASP.NET Core'),
                   (@cv3,N'synthetic-3.pdf',N'test',N'application/pdf',1,REPLICATE('3',64),N'Completed',N'Synthetic test content');
            INSERT INTO Applications(CvId,JobId) VALUES(@cv1,1),(@cv2,1),(@cv3,1);
            INSERT INTO PromptVersions(VersionName,SystemPrompt,IsActive) VALUES(N'Runtime schema fixture',N'Synthetic prompt',1);
            SET IDENTITY_INSERT EvaluationRuns ON;
            INSERT INTO EvaluationRuns(RunId,RunType,PromptVersionId,ModelName,Temperature,KeywordWeight,AiWeight,Status,TotalItems,ProcessedItems)
            VALUES(24,N'Screening',1,N'synthetic',0.2,0.3,0.7,N'Completed',3,3),
                  (25,N'Screening',1,N'synthetic',0.2,0.3,0.7,N'Completed',3,3),
                  (26,N'Screening',1,N'synthetic',0.2,0.3,0.7,N'Completed',0,0);
            SET IDENTITY_INSERT EvaluationRuns OFF;
            INSERT INTO Evaluations(RunId,ApplicationId,FinalScore,KeywordScore,AiScore,Status,ScoreSource,FitLabel,PredictedPass)
            SELECT r.RunId,a.ApplicationId,CASE WHEN a.ApplicationId=3 THEN NULL ELSE 80 END,
                   CASE WHEN a.ApplicationId=3 THEN NULL ELSE 80 END,CASE WHEN a.ApplicationId=1 THEN 80 ELSE NULL END,
                   CASE WHEN a.ApplicationId=3 THEN N'Failed' ELSE N'Completed' END,
                   CASE WHEN a.ApplicationId=2 THEN N'KeywordOnly' ELSE N'Combined' END,
                   CASE WHEN a.ApplicationId=3 THEN NULL ELSE N'Phù hợp' END,
                   CASE WHEN a.ApplicationId=3 THEN NULL ELSE 1 END
            FROM Applications a CROSS JOIN EvaluationRuns r WHERE r.RunId IN (24,25);
            """);
        var beforeHash = await LegacyFingerprint(db);
        var recorder = new RankingCommands();
        using var factory = TestSupport.ApiFactory(settings: new() { ["Scoring:WorkerEnabled"] = "false" })
            .WithWebHostBuilder(host =>
            {
                host.ConfigureLogging(logging => logging.ClearProviders());
                host.ConfigureTestServices(services =>
                {
                    services.RemoveAll<DbContextOptions<SmartCvDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                    services.AddDbContext<SmartCvDbContext>(options => options.UseSqlServer(connection).AddInterceptors(recorder));
                });
            });
        using var client = factory.CreateClient();
        using var before = await client.GetAsync("/api/runs/25/ranking");
        Assert.Equal(HttpStatusCode.InternalServerError, before.StatusCode);
        var failure = await before.Content.ReadAsStringAsync();
        Assert.Contains("SqlException", failure);
        foreach (var column in new[] { "Stage", "RejectReason", "StageUpdatedAt", "StageVersion" })
            Assert.Contains($"Invalid column name '{column}'", failure);
        using var notReady = await client.GetAsync("/api/health/database");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        using (var health = JsonDocument.Parse(await notReady.Content.ReadAsStringAsync()))
        {
            Assert.Equal("SchemaOutOfDate", health.RootElement.GetProperty("status").GetString());
            Assert.Contains("20261112045258_ApplicationStages", health.RootElement.GetProperty("pendingMigrations").EnumerateArray().Select(v => v.GetString()));
            Assert.Contains("Applications.Stage", health.RootElement.GetProperty("missingObjects").EnumerateArray().Select(v => v.GetString()));
        }

        var reviewed = await File.ReadAllTextAsync(TestSupport.RepoFile("database/runtime_apply_application_stages.sql"));
        // An unapproved target must fail without making any changes.
        var guard = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(reviewed));
        Assert.Equal(51035, guard.Number);
        Assert.Equal(beforeHash, await LegacyFingerprint(db));
        var approved = reviewed.Replace("DECLARE @ExpectedDatabase sysname = N'REVIEW_DATABASE_NAME_BEFORE_EXECUTION';",
            $"DECLARE @ExpectedDatabase sysname = N'{configured.InitialCatalog}';", StringComparison.Ordinal);
        var interrupted = approved.Replace("        EXEC(N'CREATE TABLE [ApplicationEvents]", "        THROW 51036, N'Synthetic deployment interruption', 1;\n        EXEC(N'CREATE TABLE [ApplicationEvents]", StringComparison.Ordinal);
        Assert.NotEqual(approved, interrupted);
        var interruption = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(interrupted));
        Assert.Equal(51036, interruption.Number);
        Assert.Equal(beforeHash, await LegacyFingerprint(db));
        Assert.Equal(4, (await DatabaseSchemaDiagnostics.InspectAsync(db)).MissingObjects.Count(n => n.StartsWith("Applications.", StringComparison.Ordinal)));
        await db.Database.ExecuteSqlRawAsync(approved);
        Assert.Equal(beforeHash, await LegacyFingerprint(db));
        Assert.True((await DatabaseSchemaDiagnostics.InspectAsync(db)).IsReady);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health/database")).StatusCode);
        foreach (var runId in new[] { 24, 25 })
        {
            using var response = await client.GetAsync($"/api/runs/{runId}/ranking");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var ranking = (await response.Content.ReadFromJsonAsync<RankingResponse>())!;
            Assert.Equal(3, ranking.Items.Count);
            Assert.Equal(new int?[] { 1, 1, null }, ranking.Items.Select(i => i.Rank));
            Assert.Equal(3, ranking.StageCounts["New"]);
            Assert.All(ranking.Items, i => { Assert.Null(i.CandidateName); Assert.Equal("New", i.Stage); Assert.NotEqual(Guid.Empty, i.StageVersion); Assert.Null(i.StageUpdatedAt); });
            var keywordOnly = ranking.Items.Single(i => i.ApplicationId == 2);
            Assert.Null(keywordOnly.AiScore);
            foreach (var item in ranking.Items)
            {
                using var evaluation = await client.GetAsync($"/api/evaluations/{item.EvaluationId}");
                Assert.True(evaluation.StatusCode == HttpStatusCode.OK, await evaluation.Content.ReadAsStringAsync());
            }
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/runs/26/ranking")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/runs/2147483647/ranking")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/runs/25/ranking?jobId=2147483647")).StatusCode);
        var current = (await client.GetFromJsonAsync<RankingResponse>("/api/runs/25/ranking"))!;
        var item1 = current.Items.Single(i => i.ApplicationId == 1);
        var decision = new ChangeStageRequest { ToStage = "Rejected", RejectReason = "MissingSkills", Actor = "Synthetic HR", ExpectedStageVersion = item1.StageVersion };
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync("/api/applications/1/stage", decision)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync("/api/applications/1/stage", decision)).StatusCode);
        for (var i = 0; i < 3; i++)
        {
            var reload = (await client.GetFromJsonAsync<RankingResponse>("/api/runs/25/ranking"))!;
            Assert.Equal(1, reload.StageCounts["Rejected"]);
            Assert.Equal(2, reload.StageCounts["New"]);
        }
        var bulk = new BulkChangeStageRequest { ApplicationIds = [1, 2], ToStage = "OnHold", Actor = "Synthetic HR" };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/applications/stage", bulk)).StatusCode);
        Assert.Equal(3, await db.ApplicationEvents.CountAsync());
        var forbiddenUpdate = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE ApplicationEvents SET Note=N'forbidden rewrite' WHERE EventId=1"));
        Assert.Equal(51000, forbiddenUpdate.Number);
        var forbiddenDelete = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM ApplicationEvents WHERE EventId=1"));
        Assert.Equal(51000, forbiddenDelete.Number);
        // A second deployment is a no-op: versions, current decisions and history must remain intact.
        var versions = await db.Applications.AsNoTracking().OrderBy(a => a.ApplicationId).Select(a => a.StageVersion).ToListAsync();
        await db.Database.ExecuteSqlRawAsync(approved);
        Assert.Equal(versions, await db.Applications.AsNoTracking().OrderBy(a => a.ApplicationId).Select(a => a.StageVersion).ToListAsync());
        Assert.Equal(3, await db.ApplicationEvents.CountAsync());
        Assert.Equal(beforeHash, await LegacyFingerprint(db));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatabaseSchemaDiagnostics.InspectAsync(db, cancelled.Token));
        await File.WriteAllLinesAsync(Path.Combine(evidence, "ranking-generated-sql.txt"), recorder.Commands.Distinct());
        await File.WriteAllTextAsync(Path.Combine(evidence, "runtime-sql-evidence.json"), JsonSerializer.Serialize(new
        {
            TestDatabase = configured.InitialCatalog, BeforeRanking25 = 500, SqlError = 207, BeforeReadiness = 503,
            AfterRanking24 = 200, AfterRanking25 = 200, AfterReadiness = 200, LegacyDataPreserved = true,
            MissingRun = 404, EmptyRun = 404, StaleDecision = 409, HistoryEvents = 3, RepeatedDeployment = "Unchanged",
            DatabaseRetained = true
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<string> LegacyFingerprint(SmartCvDbContext db)
    {
        // Only synthetic data in the generated test database. No values are logged, only the final hash is compared.
        await db.Database.OpenConnectionAsync();
        try
        {
            var result = new StringBuilder();
            foreach (var sql in new[] { "SELECT ApplicationId,CvId,JobId,CreatedAt FROM Applications ORDER BY ApplicationId", "SELECT * FROM Cvs ORDER BY CvId", "SELECT * FROM Evaluations ORDER BY EvaluationId", "SELECT * FROM Jobs ORDER BY JobId", "SELECT * FROM EvaluationRuns ORDER BY RunId", "SELECT * FROM PromptVersions ORDER BY PromptVersionId" })
            {
                await using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = sql;
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) for (var i = 0; i < reader.FieldCount; i++)
                    result.Append(JsonSerializer.Serialize(reader.IsDBNull(i) ? null : reader.GetValue(i))).Append('|');
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.ToString())));
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private sealed class RankingCommands : DbCommandInterceptor
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Commands = new();
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[StageVersion]") && command.CommandText.Contains("FROM [Evaluations]")) Commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class RuntimeSqlFactAttribute : FactAttribute
{
    public const string Setting = "SMARTCV_RUNTIME_TEST_SQL_CONNECTION";
    public RuntimeSqlFactAttribute()
    {
        var setting = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrWhiteSpace(setting)) { Skip = $"Set {Setting} for a new, retained SmartCv_Runtime_Test_ database."; return; }
        try { Validate(new SqlConnectionStringBuilder(setting)); }
        catch (ArgumentException) { Skip = "Runtime SQL test requires a designated SmartCv_Runtime_Test_ database without AttachDBFilename."; }
    }
    internal static void Validate(SqlConnectionStringBuilder builder)
    {
        if (!builder.InitialCatalog.StartsWith("SmartCv_Runtime_Test_", StringComparison.Ordinal) || !string.IsNullOrWhiteSpace(builder.AttachDBFilename))
            throw new ArgumentException("Only an explicitly designated runtime test connection is accepted.");
    }
}
