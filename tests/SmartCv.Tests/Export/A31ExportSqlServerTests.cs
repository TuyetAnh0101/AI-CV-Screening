using System.Data.Common;
using System.Net;
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
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.Export;

public class A31ExportSqlServerTests
{
    [A31SqlFact]
    public async Task Approved_isolated_SQL_catalog_verifies_migration_append_only_and_atomic_export_rollback()
    {
        var target = Validate(Environment.GetEnvironmentVariable("SMARTCV_A31_TEST_SQL_CONNECTION")!);
        await using var connection = new SqlConnection(target.ConnectionString); await connection.OpenAsync();
        await using (var check = new SqlCommand("SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0", connection))
            Assert.Equal(0, (int)(await check.ExecuteScalarAsync())!);
        await using var db = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(target.ConnectionString).Options);
        var sql = db.GetService<IMigrator>().GenerateScript(toMigration: A31ExportModelTests.MigrationId);
        Assert.DoesNotMatch(@"(?i)\b(CREATE|DROP)\s+DATABASE\b|(?im)^\s*USE\s+", sql);
        foreach (var batch in Regex.Split(sql, @"(?im)^\s*GO\s*\r?$").Where(s => !string.IsNullOrWhiteSpace(s)))
        { await using var command = new SqlCommand(batch, connection) { CommandTimeout = 120 }; await command.ExecuteNonQueryAsync(); }
        var fault = new FailExportInsert();
        using var factory = A28TestSupport.Factory(new() { ["Scoring:WorkerEnabled"] = "false", ["Retention:AutoAnonymize"] = "false" })
            .WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<SmartCvDbContext>>(); services.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                services.AddDbContext<SmartCvDbContext>(o => o.UseSqlServer(target.ConnectionString).AddInterceptors(fault));
            }));
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        var job = await db.Jobs.FirstAsync();
        var run = new EvaluationRun { RunType = "Screening", Status = "Completed", ModelName = "synthetic", TotalItems = 1,
            ProcessedItems = 1, KeywordWeight = .3m, AiWeight = .7m, CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
            PromptVersion = new PromptVersion { VersionName = "A31 SQL", SystemPrompt = "Synthetic" }, Evaluations = [new Evaluation
            {
                Status = "Completed", FinalScore = 90, KeywordScore = 90, AiScore = 90, ScoreSource = "Combined", FitLabel = "Rất phù hợp", PredictedPass = true,
                Application = new Application { JobId = job.JobId, Stage = "Shortlisted", Cv = new Cv { CvId = Guid.NewGuid(),
                    OriginalFileName = "synthetic.pdf", StoredPath = "not-used", ContentType = "application/pdf", FileHash = new string('e', 64), ExtractionStatus = "Completed" } }
            }] };
        db.Add(run); await db.SaveChangesAsync(); using var http = A29AuthorizationTests.Client(factory, user);
        using var success = await http.GetAsync($"/api/runs/{run.RunId}/ranking/export"); Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal(1, await db.ExportLogs.CountAsync());
        fault.Enabled = true;
        using var failure = await http.GetAsync($"/api/runs/{run.RunId}/ranking/export"); Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.Equal(1, await db.ExportLogs.CountAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.ActionCode == "runs.ranking_exported" && e.Outcome == "AccessGranted"));
        foreach (var statement in new[] { "UPDATE dbo.ExportLogs SET RowCount=2", "DELETE FROM dbo.ExportLogs" })
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(statement)); Assert.Equal(51031, error.Number);
        }
        // Keep the approved catalog and all synthetic rows for review. Never creates/drops a database.
    }

    [Theory]
    [InlineData("Server=never-connect.invalid;Database=SmartCvDb;Integrated Security=true")]
    [InlineData("Server=never-connect.invalid;Database=master;Integrated Security=true")]
    [InlineData("Server=never-connect.invalid;Database=SmartCv_A31_Test_old;Integrated Security=true")]
    public void Unsafe_targets_are_rejected_before_any_connection(string value) => Assert.Throws<ArgumentException>(() => Validate(value));

    private static SqlConnectionStringBuilder Validate(string text)
    {
        var target = new SqlConnectionStringBuilder(text);
        if (string.IsNullOrWhiteSpace(target.DataSource) || !string.IsNullOrWhiteSpace(target.AttachDBFilename)
            || !Regex.IsMatch(target.InitialCatalog, "^SmartCv_A31_Test_[a-f0-9]{32}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("A31 requires an isolated GUID test catalog.");
        return target;
    }
    private sealed class FailExportInsert : DbCommandInterceptor
    {
        internal bool Enabled;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (Enabled && command.CommandText.Contains("INSERT INTO [ExportLogs]", StringComparison.Ordinal))
                throw new DbUpdateException("Synthetic failure after staged AuditEvent insert.");
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class A31SqlFact : FactAttribute
{
    public A31SqlFact()
    {
        if (Environment.GetEnvironmentVariable("SMARTCV_A31_SQL_APPROVED") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SMARTCV_A31_TEST_SQL_CONNECTION")))
            Skip = "Requires an explicitly approved, pre-provisioned, empty SmartCv_A31_Test_<GUID> SQL catalog; never use SmartCvDb.";
    }
}
