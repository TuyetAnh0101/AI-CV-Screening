using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Jobs;

public class JobTitleIndexSqlServerTests
{
    [A20SqlFact]
    public async Task EF_Up_rename_duplicate_Down_and_reapply_preserve_all_data_and_relationships()
    {
        await using var env = await A20SqlEnvironment.CreateAsync();
        await env.SeedBusinessDataAsync();
        var before = await env.SnapshotAsync("before-up");
        Assert.Equal(5, before.Counts["Jobs"]);
        Assert.Equal(29, before.Counts["JobSkills"]);
        Assert.Equal(52, before.Counts["JobSkillAliases"]);
        Assert.Equal(2, before.Counts["Cvs"]);
        Assert.Equal(2, before.Counts["Applications"]);
        Assert.Equal(2, before.Counts["Evaluations"]);
        Assert.Equal((true, true), await env.TitleIndexAsync());
        var oldHistory = await env.HistoryAsync();
        Assert.Equal(4, oldHistory.Length);
        Assert.Equal(JobTitleIndexTests.PreviousMigration, oldHistory[^1]);

        await env.MigrateAsync(JobTitleIndexTests.MigrationId);
        Assert.Equal((true, false), await env.TitleIndexAsync());
        AssertSnapshot(before, await env.SnapshotAsync("after-up"));
        var newHistory = await env.HistoryAsync();
        Assert.Equal(oldHistory.Append(JobTitleIndexTests.MigrationId), newHistory);
        await RenameAndRestoreAsync(env);
        Assert.Equal(2601, await DuplicateRenameAsync(env));
        var collation = await env.ScalarStringAsync("SELECT CONVERT(nvarchar(128),DATABASEPROPERTYEX(DB_NAME(),'Collation'));");
        if (collation.Contains("_CI_", StringComparison.OrdinalIgnoreCase))
            Assert.Equal(2601, await DuplicateRenameAsync(env, caseVariant: true));
        await env.RunVerificationAsync();
        AssertSnapshot(before, await env.SnapshotAsync("after-renames"));

        await env.MigrateAsync(JobTitleIndexTests.PreviousMigration); // Roll back ONLY A20.
        Assert.Equal((true, true), await env.TitleIndexAsync());
        Assert.Equal(oldHistory, await env.HistoryAsync());
        Assert.Equal(2627, await DuplicateRenameAsync(env));
        AssertSnapshot(before, await env.SnapshotAsync("after-down"));
        await env.MigrateAsync(JobTitleIndexTests.MigrationId);
        await env.MigrateAsync(JobTitleIndexTests.MigrationId); // Already-applied upgrade is a no-op.
        Assert.Equal((true, false), await env.TitleIndexAsync());
        Assert.Equal(newHistory, await env.HistoryAsync());
        AssertSnapshot(before, await env.SnapshotAsync("after-reapply"));
        await env.WriteAsync("ef-up-down-result", new { Index = "UQ_Jobs_Title", Rename = true, DuplicateUp = 2601, DuplicateDown = 2627, Collation = collation, DataPreserved = true, ForeignKeysPreserved = true, History = newHistory });
    }

    [A20SqlFact]
    public async Task Exported_delta_SQL_matches_EF_and_supports_Up_and_Down_without_other_schema_changes()
    {
        await using var env = await A20SqlEnvironment.CreateAsync();
        await env.SeedBusinessDataAsync();
        var before = await env.SnapshotAsync("script-before");
        var committed = await File.ReadAllTextAsync(TestSupport.RepoFile("database/a20_job_title_unique_index.sql"));
        await using var db = env.Context();
        var migrator = db.GetService<IMigrator>();
        var generated = migrator.GenerateScript(JobTitleIndexTests.PreviousMigration, JobTitleIndexTests.MigrationId);
        Assert.Equal(Normalize(GuardedScript(generated)), Normalize(committed));
        await env.ExecuteScriptAsync(committed);
        Assert.Equal((true, false), await env.TitleIndexAsync());
        Assert.Equal(JobTitleIndexTests.MigrationId, (await env.HistoryAsync())[^1]);
        await env.RunVerificationAsync();
        AssertSnapshot(before, await env.SnapshotAsync("script-after-up"));
        var rollback = GuardedScript(migrator.GenerateScript(JobTitleIndexTests.MigrationId, JobTitleIndexTests.PreviousMigration));
        await env.WriteTextAsync("rollback.sql", rollback);
        await env.ExecuteScriptAsync(rollback);
        Assert.Equal((true, true), await env.TitleIndexAsync());
        Assert.Equal(JobTitleIndexTests.PreviousMigration, (await env.HistoryAsync())[^1]);
        AssertSnapshot(before, await env.SnapshotAsync("script-after-down"));
    }

    [A20SqlFact]
    public async Task Unexpected_FK_to_Title_causes_transactional_failure_without_dropping_the_FK_or_data()
    {
        await using var env = await A20SqlEnvironment.CreateAsync();
        await env.SeedBusinessDataAsync();
        await env.ExecuteScriptAsync("""
            CREATE TABLE dbo.A20_TitleReference (
                ReferenceId int NOT NULL PRIMARY KEY, Title nvarchar(150) NOT NULL,
                CONSTRAINT FK_A20_TitleReference_Title FOREIGN KEY (Title) REFERENCES dbo.Jobs(Title));
            INSERT INTO dbo.A20_TitleReference(ReferenceId,Title) SELECT TOP (1) 1,Title FROM dbo.Jobs ORDER BY JobId;
            """);
        var before = await env.SnapshotAsync("fk-blocker-before");
        var exception = await Assert.ThrowsAsync<SqlException>(() => env.MigrateAsync(JobTitleIndexTests.MigrationId));
        Assert.Contains(exception.Errors.Cast<SqlError>(), error => error.Number is 3725 or 3727);
        Assert.Equal((true, true), await env.TitleIndexAsync());
        Assert.Equal(JobTitleIndexTests.PreviousMigration, (await env.HistoryAsync())[^1]);
        AssertSnapshot(before, await env.SnapshotAsync("fk-blocker-after"));
        var exported = await File.ReadAllTextAsync(TestSupport.RepoFile("database/a20_job_title_unique_index.sql"));
        var scriptError = await Assert.ThrowsAsync<SqlException>(() => env.ExecuteScriptAsync(exported));
        Assert.Contains(scriptError.Errors.Cast<SqlError>(), error => error.Number is 3725 or 3727);
        Assert.Equal(JobTitleIndexTests.PreviousMigration, (await env.HistoryAsync())[^1]);
        Assert.Equal((true, true), await env.TitleIndexAsync());
        AssertSnapshot(before, await env.SnapshotAsync("fk-blocker-script-after"));
        Assert.Equal(1, await env.ScalarIntAsync("SELECT COUNT(*) FROM dbo.A20_TitleReference;"));
        await env.WriteAsync("fk-blocker-result", new { ExpectedError = exception.Number, AutomaticFkDrop = false, DataPreserved = true });
    }

    [A20SqlFact]
    public async Task SQL_sql_bootstrap_has_the_old_constraint_and_no_migration_history_and_requires_reconciliation()
    {
        await using var env = await A20SqlEnvironment.CreateAsync(migrateBaseline: false);
        var bootstrap = await File.ReadAllTextAsync(TestSupport.RepoFile("database/SQL.sql"));
        var start = bootstrap.IndexOf("CREATE TABLE Candidates", StringComparison.Ordinal);
        Assert.True(start > 0);
        // Exclude the original CREATE DATABASE / USE SmartCvDb header. Execute schema only in OUR new catalog.
        await env.ExecuteScriptAsync(bootstrap[start..]);
        await env.ExecuteScriptAsync("INSERT INTO dbo.Jobs(Title) VALUES (N'A20 legacy one'),(N'A20 legacy two');");
        Assert.Equal((true, true), await env.TitleIndexAsync());
        Assert.Equal(0, await env.ScalarIntAsync("SELECT COUNT(*) FROM sys.tables WHERE name=N'__EFMigrationsHistory';"));
        Assert.Equal(0, await env.ScalarIntAsync("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.Applications') AND name=N'Stage';"));
        Assert.Equal(0, await env.ScalarIntAsync("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.Cvs') AND name=N'AnonymizedAt';"));
        Assert.Equal(2, await env.ScalarIntAsync("SELECT COUNT(*) FROM dbo.Jobs;"));
        await env.WriteAsync("legacy-schema-result", new { UniqueConstraint = true, EfHistoryExists = false, HasA35Stages = false, HasA38Retention = false, JobsPreserved = 2, AutomaticMigrationOrHistoryRepair = false });
    }

    private static async Task RenameAndRestoreAsync(A20SqlEnvironment env)
    {
        await using var db = env.Context();
        await using var transaction = await db.Database.BeginTransactionAsync();
        // A20's migration target predates A21 columns; read only the fields this test exercises.
        var job = await db.Jobs.OrderBy(j => j.JobId).Select(j => new Job { JobId = j.JobId, Title = j.Title }).FirstAsync();
        db.Attach(job);
        var original = job.Title;
        job.Title = "Kỹ sư Backend A20 — đổi tên thử";
        await db.SaveChangesAsync();
        Assert.Equal(job.Title, await db.Jobs.AsNoTracking().Where(j => j.JobId == job.JobId).Select(j => j.Title).SingleAsync());
        job.Title = original;
        await db.SaveChangesAsync();
        Assert.Equal(original, await db.Jobs.AsNoTracking().Where(j => j.JobId == job.JobId).Select(j => j.Title).SingleAsync());
        await transaction.RollbackAsync();
    }

    private static async Task<int> DuplicateRenameAsync(A20SqlEnvironment env, bool caseVariant = false)
    {
        await using var db = env.Context();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var jobs = await db.Jobs.OrderBy(j => j.JobId).Take(2).Select(j => new Job { JobId = j.JobId, Title = j.Title }).ToListAsync();
        db.AttachRange(jobs);
        jobs[1].Title = caseVariant ? jobs[0].Title.ToUpperInvariant() : jobs[0].Title;
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var sql = Assert.IsType<SqlException>(error.InnerException);
        Assert.Contains("UQ_Jobs_Title", sql.Message);
        await transaction.RollbackAsync();
        return sql.Number;
    }

    private static void AssertSnapshot(A20Snapshot expected, A20Snapshot actual)
    {
        Assert.Equal(expected.DataHash, actual.DataHash);
        Assert.Equal(expected.SchemaHash, actual.SchemaHash);
        Assert.Equal(expected.Counts.OrderBy(p => p.Key), actual.Counts.OrderBy(p => p.Key));
    }
    private static string Normalize(string sql) => sql.TrimStart('\uFEFF').Replace("\r\n", "\n").Trim();
    private static string GuardedScript(string generated) => "SET XACT_ABORT ON;\nBEGIN TRY\n"
        + Regex.Replace(generated.Trim(), @"(?im)^GO\s*$", "").Trim()
        + "\nEND TRY\nBEGIN CATCH\n    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;\n    THROW;\nEND CATCH;\nGO";
}

public sealed class A20SqlFactAttribute : FactAttribute
{
    public const string Setting = "SMARTCV_A20_TEST_SQL_CONNECTION";
    public const string Prefix = "SmartCv_A20_Test_";
    public A20SqlFactAttribute()
    {
        var value = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrWhiteSpace(value)) { Skip = $"NOT RUN: set {Setting} for a new retained {Prefix} catalog."; return; }
        try { Validate(new SqlConnectionStringBuilder(value)); }
        catch (ArgumentException) { Skip = "NOT RUN: A20 requires its test catalog prefix and no AttachDBFilename."; }
    }
    internal static void Validate(SqlConnectionStringBuilder connection)
    {
        if (!connection.InitialCatalog.StartsWith(Prefix, StringComparison.Ordinal) || !string.IsNullOrEmpty(connection.AttachDBFilename))
            throw new ArgumentException("Only designated A20 test catalogs without attached files are accepted.");
    }
}

internal sealed record A20Snapshot(Dictionary<string, int> Counts, string DataHash, string SchemaHash);

internal sealed class A20SqlEnvironment : IAsyncDisposable
{
    private readonly string connection;
    private readonly string catalog;
    private A20SqlEnvironment(string value, string name) { connection = value; catalog = name; }
    internal SmartCvDbContext Context() => new(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options);

    internal static async Task<A20SqlEnvironment> CreateAsync(bool migrateBaseline = true)
    {
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A20SqlFactAttribute.Setting));
        A20SqlFactAttribute.Validate(builder);
        builder.InitialCatalog = A20SqlFactAttribute.Prefix + Guid.NewGuid().ToString("N");
        builder.Pooling = false; builder.ConnectTimeout = 10;
        Assert.Matches("^SmartCv_A20_Test_[0-9a-f]{32}$", builder.InitialCatalog);
        var env = new A20SqlEnvironment(builder.ConnectionString, builder.InitialCatalog);
        var master = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" };
        await using (var sql = new SqlConnection(master.ConnectionString))
        {
            await sql.OpenAsync();
            await using var check = new SqlCommand("SELECT DB_ID(@catalog);", sql);
            check.Parameters.AddWithValue("@catalog", builder.InitialCatalog);
            Assert.Equal(DBNull.Value, await check.ExecuteScalarAsync());
            // Generated/validated identifier only. MULTI_USER applies only to this new database,
            // which otherwise inherits this workstation's SINGLE_USER model setting.
            await using var create = new SqlCommand($"CREATE DATABASE [{builder.InitialCatalog}]; ALTER DATABASE [{builder.InitialCatalog}] SET MULTI_USER;", sql);
            await create.ExecuteNonQueryAsync();
        }
        await env.WriteAsync("catalog", new { Catalog = builder.InitialCatalog, Retained = true });
        if (migrateBaseline) await env.MigrateAsync(JobTitleIndexTests.PreviousMigration);
        return env;
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var sql = new SqlConnection(connection);
        try
        {
            await sql.OpenAsync();
            await using var command = new SqlCommand("SELECT DB_NAME();", sql);
            Assert.Equal(catalog, await command.ExecuteScalarAsync());
            return sql;
        }
        catch { await sql.DisposeAsync(); throw; }
    }

    internal async Task MigrateAsync(string target)
    {
        Assert.Contains(target, new[] { JobTitleIndexTests.PreviousMigration, JobTitleIndexTests.MigrationId });
        await using var targetCheck = await OpenAsync();
        await using var db = Context();
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    internal async Task<string[]> HistoryAsync()
    {
        await using var db = Context();
        return (await db.Database.GetAppliedMigrationsAsync()).ToArray();
    }

    internal async Task<(bool Unique, bool Constraint)> TitleIndexAsync()
    {
        await using var sql = await OpenAsync();
        await using var command = new SqlCommand("""
            SELECT i.is_unique,i.is_unique_constraint,c.name AS ColumnName,
                (SELECT COUNT(*) FROM sys.index_columns keys WHERE keys.object_id=i.object_id AND keys.index_id=i.index_id AND keys.key_ordinal>0) AS KeyCount
            FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1
            JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE i.object_id=OBJECT_ID(N'dbo.Jobs') AND i.name=N'UQ_Jobs_Title';
            """, sql);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Title", reader.GetString(2)); Assert.Equal(1, reader.GetInt32(3));
        var result = (reader.GetBoolean(0), reader.GetBoolean(1));
        Assert.False(await reader.ReadAsync());
        return result;
    }

    internal async Task SeedBusinessDataAsync()
    {
        await using var db = Context();
        Assert.Equal(5, await db.Jobs.CountAsync());
        var at = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
        var prompt = new PromptVersion { VersionName = "A20 synthetic", SystemPrompt = "Synthetic; never sent to AI.", CreatedAt = at };
        var set = new EvaluationSet { Name = "A20 synthetic set", CreatedAt = at };
        var run = new EvaluationRun { PromptVersion = prompt, RunType = RunTypes.Screening, ModelName = "synthetic", KeywordWeight = .3m, AiWeight = .7m, Status = RunStatuses.Completed, TotalItems = 2, ProcessedItems = 2, CreatedAt = at, CompletedAt = at };
        for (var i = 1; i <= 2; i++)
        {
            var cv = new Cv { CvId = Guid.NewGuid(), OriginalFileName = "synthetic-a20.pdf", StoredPath = "synthetic-a20/no-file", ContentType = "application/pdf", FileSizeBytes = 100, FileHash = Guid.NewGuid().ToString("N").PadRight(64,'0'), UploadedAt = at, ExtractionStatus = ExtractionStatuses.Completed, ExtractedText = "Synthetic SQL and C# skill evidence.", ConsentConfirmedAt = at, RetainUntil = at.AddMonths(6) };
            var app = new Application { Cv = cv, JobId = i, CreatedAt = at, Stage = ApplicationStages.Interview, StageUpdatedAt = at.AddHours(2) };
            app.Events.Add(new ApplicationEvent { FromStage = ApplicationStages.New, ToStage = ApplicationStages.Interview, Actor = "Synthetic HR", CreatedAt = at.AddHours(2) });
            var evaluation = new Evaluation { Run = run, Application = app, KeywordScore = 70, AiScore = 80, FinalScore = 77, ScoreSource = ScoreSources.Combined, FitLabel = "Good", Status = EvaluationStatuses.Completed, PredictedPass = true, Reasoning = "Synthetic saved evidence", CreatedAt = at };
            evaluation.Skills.Add(new EvaluationSkill { JobId = i, SkillCode = "csharp", IsMatched = true, Evidence = "Synthetic C#" });
            db.Add(evaluation);
            db.Add(new HrLabel { Application = app, EvaluationSet = set, IsPass = true, LabeledBy = "Synthetic HR", LabeledAt = at });
        }
        await db.SaveChangesAsync();
    }

    internal async Task ExecuteScriptAsync(string script)
    {
        Assert.DoesNotMatch(@"(?im)^\s*(?:USE\s|CREATE\s+DATABASE\s|DROP\s+DATABASE\s)", script);
        await using var sql = await OpenAsync();
        try
        {
            foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*$").Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                await using var command = new SqlCommand(batch, sql);
                await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await using var rollback = new SqlCommand("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", sql);
            await rollback.ExecuteNonQueryAsync();
        }
    }

    internal async Task RunVerificationAsync()
    {
        var script = await File.ReadAllTextAsync(TestSupport.RepoFile("database/a20_kiem_tra_doi_ten_job.sql"));
        await ExecuteScriptAsync(script); // Assertions in SQL THROW on any failure; both attempts always roll back.
    }

    internal async Task<int> ScalarIntAsync(string script)
    {
        await using var sql = await OpenAsync();
        await using var command = new SqlCommand(script, sql);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    internal async Task<string> ScalarStringAsync(string script)
    {
        await using var sql = await OpenAsync();
        await using var command = new SqlCommand(script, sql);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    internal async Task<A20Snapshot> SnapshotAsync(string label)
    {
        var tables = new (string Table, string Key)[]
        {
            ("Candidates","CandidateId"),("Jobs","JobId"),("JobSkills","JobId,SkillCode"),("JobSkillAliases","JobId,SkillCode,Alias"),
            ("Cvs","CvId"),("Applications","ApplicationId"),("ApplicationEvents","EventId"),("PromptVersions","PromptVersionId"),
            ("EvaluationSets","EvaluationSetId"),("HrLabels","EvaluationSetId,ApplicationId"),("EvaluationRuns","RunId"),
            ("Evaluations","EvaluationId"),("EvaluationSkills","EvaluationId,JobId,SkillCode")
        };
        var data = new Dictionary<string, List<Dictionary<string, object?>>>();
        await using var sql = await OpenAsync();
        foreach (var (table, key) in tables)
        {
            await using var command = new SqlCommand($"SELECT * FROM dbo.[{table}] ORDER BY {key};", sql); // Fixed table/key allowlist above.
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync())
                rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i)));
            data[table] = rows;
        }
        var json = JsonSerializer.Serialize(data);
        await WriteTextAsync(label + "-synthetic-data.json", json); // Full data snapshot of ONLY the synthetic catalog.
        await using var schemaCommand = new SqlCommand("""
            SELECT N'FK' AS Kind,f.name AS Name,OBJECT_NAME(f.parent_object_id) AS Parent,
                COL_NAME(c.parent_object_id,c.parent_column_id) AS ColumnName,OBJECT_NAME(f.referenced_object_id) AS Target,
                COL_NAME(c.referenced_object_id,c.referenced_column_id) AS TargetColumn,f.delete_referential_action_desc COLLATE DATABASE_DEFAULT AS Detail
            FROM sys.foreign_keys f JOIN sys.foreign_key_columns c ON c.constraint_object_id=f.object_id
            UNION ALL SELECT N'DEFAULT',d.name,OBJECT_NAME(d.parent_object_id),COL_NAME(d.parent_object_id,d.parent_column_id),N'',N'',d.definition FROM sys.default_constraints d
            UNION ALL SELECT N'CHECK',c.name,OBJECT_NAME(c.parent_object_id),N'',N'',N'',c.definition FROM sys.check_constraints c
            UNION ALL SELECT N'TRIGGER',t.name,OBJECT_NAME(t.parent_id),N'',N'',N'',OBJECT_DEFINITION(t.object_id) FROM sys.triggers t WHERE t.parent_class=1
            ORDER BY Kind,Name,ColumnName;
            """, sql);
        var schema = new StringBuilder();
        await using (var reader = await schemaCommand.ExecuteReaderAsync())
            while (await reader.ReadAsync()) for (var i = 0; i < reader.FieldCount; i++) schema.Append(JsonSerializer.Serialize(reader.GetValue(i))).Append('|');
        var snapshot = new A20Snapshot(data.ToDictionary(p => p.Key, p => p.Value.Count), Hash(json), Hash(schema.ToString()));
        await WriteAsync(label + "-fingerprint", snapshot);
        return snapshot;
    }

    internal Task WriteAsync(string label, object value) => WriteTextAsync(label + ".json", JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    internal Task WriteTextAsync(string suffix, string text)
    {
        var directory = TestSupport.RepoFile("artifacts/a20-sql");
        Directory.CreateDirectory(directory);
        return File.WriteAllTextAsync(Path.Combine(directory, catalog + "-" + suffix), text);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask; // Retain every catalog; no worker, Gemini or database cleanup.
}
