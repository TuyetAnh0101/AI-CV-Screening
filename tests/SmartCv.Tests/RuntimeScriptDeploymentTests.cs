using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

/// <summary>
/// database/runtime_apply_application_stages.sql is deployed with ODBC sqlcmd. By default sqlcmd decodes a file without BOM
/// with the ANSI code page (1252) and runs with QUOTED_IDENTIFIER OFF; the A35 trigger then got a corrupted Vietnamese
/// message and different SET options than the EF Core migration, while the script still reported "ready".
/// </summary>
public class RuntimeScriptDeploymentTests
{
    internal const string ScriptPath = "database/runtime_apply_application_stages.sql";
    internal const string Placeholder = "DECLARE @ExpectedDatabase sysname = N'REVIEW_DATABASE_NAME_BEFORE_EXECUTION';";

    internal static string MigrationTriggerMessage()
    {
        var migration = File.ReadAllText(TestSupport.RepoFile("src/SmartCv.Infrastructure/Data/Migrations/20261112045258_ApplicationStages.cs"));
        const string start = "THROW 51000, N''";
        var from = migration.IndexOf(start, StringComparison.Ordinal) + start.Length;
        return migration[from..migration.IndexOf("'', 1;", from, StringComparison.Ordinal)];
    }

    [Fact]
    public void Script_is_saved_with_utf8_bom_so_sqlcmd_decodes_vietnamese_text()
    {
        var bytes = File.ReadAllBytes(TestSupport.RepoFile(ScriptPath));
        Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "Without a UTF-8 BOM, sqlcmd (no -f 65001) reads the file as Windows-1252 and corrupts the trigger message.");
    }

    [Fact]
    public void Preflight_guards_run_before_any_schema_change_and_trigger_text_matches_migration()
    {
        var script = File.ReadAllText(TestSupport.RepoFile(ScriptPath));
        var transaction = script.IndexOf("BEGIN TRANSACTION;", StringComparison.Ordinal);
        var codePageGuard = script.IndexOf("UNICODE(N'ị') <> 7883", StringComparison.Ordinal);
        var quotedIdentifierGuard = script.IndexOf("SESSIONPROPERTY('QUOTED_IDENTIFIER') <> 1", StringComparison.Ordinal);
        Assert.True(codePageGuard > 0 && codePageGuard < transaction, "Code-page guard must run before the transaction.");
        Assert.True(quotedIdentifierGuard > 0 && quotedIdentifierGuard < transaction, "QUOTED_IDENTIFIER guard must run before the transaction.");

        var message = MigrationTriggerMessage();
        Assert.Equal(58, message.Length);
        Assert.Contains($"THROW 51000, N''''{message}'''', 1;", script, StringComparison.Ordinal); // created trigger
        Assert.Contains($"CHARINDEX(N'{message}', definition)", script, StringComparison.Ordinal);  // post-condition
    }

    [RuntimeSqlFact]
    public async Task Sqlcmd_default_decoding_or_quoted_identifier_off_stop_before_changes_and_correct_run_matches_migration()
    {
        var configured = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(RuntimeSqlFactAttribute.Setting));
        RuntimeSqlFactAttribute.Validate(configured);
        configured.InitialCatalog = "SmartCv_Runtime_Test_" + Guid.NewGuid().ToString("N");
        configured.Pooling = false;
        configured.ConnectTimeout = 10;
        var evidence = TestSupport.RepoFile("artifacts/a35-runtime-tests");
        Directory.CreateDirectory(evidence);
        await File.AppendAllTextAsync(Path.Combine(evidence, "retained-test-databases.txt"),
            $"{configured.DataSource} {configured.InitialCatalog} (RuntimeScriptDeploymentTests, synthetic data)" + Environment.NewLine);

        await using var db = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(configured.ConnectionString).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261106142155_SeedJobs");
        await db.Database.ExecuteSqlRawAsync("""
            DECLARE @cv uniqueidentifier = NEWID();
            INSERT INTO Cvs(CvId,OriginalFileName,StoredPath,ContentType,FileSizeBytes,FileHash,ExtractionStatus)
            VALUES (@cv,N'synthetic.pdf',N'test',N'application/pdf',1,REPLICATE('D',64),N'Completed');
            INSERT INTO Applications(CvId,JobId) VALUES (@cv,1);
            """);

        var bytes = await File.ReadAllBytesAsync(TestSupport.RepoFile(ScriptPath));
        var body = bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes[3..] : bytes;
        string Approve(string script) => script.Replace(Placeholder, $"DECLARE @ExpectedDatabase sysname = N'{configured.InitialCatalog}';", StringComparison.Ordinal);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var asSqlcmdDefault = Approve(Encoding.GetEncoding(1252).GetString(body));  // what sqlcmd reads from a copy saved without BOM, no -f 65001
        var asUtf8 = Approve(new UTF8Encoding(false).GetString(body));
        Assert.NotEqual(asUtf8, asSqlcmdDefault);

        await db.Database.OpenConnectionAsync();
        try
        {
            var wrongCodePage = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(asSqlcmdDefault));
            Assert.Equal(51035, wrongCodePage.Number);
            Assert.Contains("-f 65001", wrongCodePage.Message);
            await AssertNotMigrated(db);

            await db.Database.ExecuteSqlRawAsync("SET QUOTED_IDENTIFIER OFF;");
            var quotedIdentifierOff = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(asUtf8));
            Assert.Equal(51035, quotedIdentifierOff.Number);
            Assert.Contains("-I", quotedIdentifierOff.Message);
            await db.Database.ExecuteSqlRawAsync("SET QUOTED_IDENTIFIER ON;");
            await AssertNotMigrated(db);

            await db.Database.ExecuteSqlRawAsync(asUtf8);
            Assert.True((await DatabaseSchemaDiagnostics.InspectAsync(db)).IsReady);
            Assert.Equal(1, await Scalar(db, "SELECT uses_quoted_identifier FROM sys.sql_modules WHERE object_id = OBJECT_ID(N'dbo.TR_ApplicationEvents_NoChange')"));
            var blocked = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE ApplicationEvents SET Note = N'x' WHERE 1 = 0"));
            Assert.Equal(51000, blocked.Number);
            Assert.Equal(MigrationTriggerMessage(), blocked.Message);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static async Task AssertNotMigrated(SmartCvDbContext db)
    {
        var readiness = await DatabaseSchemaDiagnostics.InspectAsync(db);
        Assert.Contains("20261112045258_ApplicationStages", readiness.PendingMigrations);
        Assert.Equal(17, readiness.MissingObjects.Count);
        Assert.Equal(0, await Scalar(db, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Applications') AND name IN (N'Stage', N'RejectReason', N'StageUpdatedAt', N'StageVersion')"));
    }

    private static async Task<int> Scalar(SmartCvDbContext db, string sql)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
