using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

/// <summary>
/// B43 HTTP 503 investigation: SmartCvDb had no AuditEvents table, and the idempotent script for AddAuditEvents
/// failed with Msg 156 because CREATE TRIGGER sat inside IF ... BEGIN ... END. Script generation never connects.
/// </summary>
public class B43MigrationScriptTests
{
    private const string From = "20261119110208_AddUsers";
    private const string To = "20261120043759_AddAuditEvents";

    private static string Generate(MigrationsSqlGenerationOptions options)
    {
        var db = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>()
            .UseSqlServer("Server=never-connect.invalid;Database=SmartCv_B43_Script;Integrated Security=true").Options);
        return db.GetService<IMigrator>().GenerateScript(From, To, options);
    }

    [Fact]
    public void Idempotent_script_keeps_CREATE_TRIGGER_first_in_its_own_batch()
    {
        var sql = Generate(MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("EXEC(N'CREATE TRIGGER [dbo].[TR_AuditEvents_NoChange]", sql);
        // A bare CREATE TRIGGER inside the generated IF NOT EXISTS block is a syntax error on SQL Server.
        Assert.DoesNotMatch(new Regex(@"(?im)^\s*CREATE\s+TRIGGER"), sql);
        Assert.Contains("N''Audit events are append-only.''", sql);
        Assert.DoesNotMatch(new Regex(@"(?i)\b(CREATE|DROP)\s+DATABASE\b|(?im)^\s*USE\s+"), sql);
    }

    [Fact]
    public void Non_idempotent_script_still_creates_the_append_only_trigger_and_history_row()
    {
        var sql = Generate(MigrationsSqlGenerationOptions.Default);
        Assert.Contains("CREATE TABLE [AuditEvents]", sql);
        Assert.Contains("AFTER UPDATE, DELETE", sql);
        Assert.Contains("THROW 51043", sql);
        Assert.Contains($"N'{To}'", sql);
        Assert.DoesNotContain("ApplicationEvents", sql); // A35 trigger stays untouched.
    }

    [Fact]
    public void Deployment_script_is_guarded_and_contains_the_fixed_trigger()
    {
        var script = File.ReadAllText(TestSupport.RepoFile(Path.Combine("database", "b43_add_audit_events.sql")));
        Assert.Contains("DECLARE @ExpectedDatabase sysname = N'REVIEW_DATABASE_NAME_BEFORE_EXECUTION';", script);
        Assert.Contains("SESSIONPROPERTY('QUOTED_IDENTIFIER')", script);
        Assert.Contains($"[MigrationId] = N'{From}'", script);
        Assert.Contains("EXEC(N'CREATE TRIGGER [dbo].[TR_AuditEvents_NoChange]", script);
        Assert.DoesNotMatch(new Regex(@"(?im)^\s*CREATE\s+TRIGGER"), script);
        Assert.DoesNotMatch(new Regex(@"(?im)^\s*USE\s+|\b(DROP|TRUNCATE)\s+TABLE\b|\bDELETE\s+FROM\b|\bUPDATE\s+\["), script);
        // Guards run before BEGIN TRANSACTION in the same batch: a failed guard aborts before any DDL.
        Assert.True(script.IndexOf("THROW 51044", StringComparison.Ordinal) < script.IndexOf("BEGIN TRANSACTION", StringComparison.Ordinal));
        Assert.DoesNotMatch(new Regex(@"(?im)^GO\s*$[\s\S]*BEGIN TRANSACTION"), script.Substring(0, script.IndexOf("COMMIT;", StringComparison.Ordinal)));
    }
}
