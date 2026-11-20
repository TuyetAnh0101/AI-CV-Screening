using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Data.Migrations;

namespace SmartCv.Tests.Export;

public class A31ExportModelTests
{
    public const string MigrationId = "20261120114152_AddRankingExportLogs";
    [Fact]
    public void Migration_only_adds_export_metadata_and_no_cascades_or_personal_data()
    {
        var migration = new AddRankingExportLogs { ActiveProvider = "Microsoft.EntityFrameworkCore.SqlServer" };
        var table = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        Assert.Equal("ExportLogs", table.Name);
        Assert.Equal(new[] { "AuditEvents", "EvaluationRuns", "Jobs" }, table.ForeignKeys.Select(f => f.PrincipalTable).OrderBy(x => x));
        Assert.DoesNotContain(migration.UpOperations, op => op is AlterColumnOperation or DropTableOperation or UpdateDataOperation or DeleteDataOperation);
        Assert.All(migration.UpOperations.OfType<CreateIndexOperation>(), i => Assert.Equal("ExportLogs", i.Table));
        Assert.Contains(migration.UpOperations.OfType<SqlOperation>(), op => op.Sql.Contains("TR_ExportLogs_NoChange"));
        using var db = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>()
            .UseSqlServer("Server=never-connect.invalid;Database=SmartCv_A31_ModelOnly;Integrated Security=true").Options);
        var model = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(ExportLog))!;
        Assert.All(model.GetForeignKeys(), fk => Assert.Equal(DeleteBehavior.NoAction, fk.DeleteBehavior));
        Assert.Contains(model.GetIndexes(), i => i.IsUnique && i.Properties.Single().Name == "AuditEventId");
        Assert.DoesNotContain(model.GetProperties(), p => new[] { "Search", "CandidateName", "FileName", "Email", "Password", "Body" }.Contains(p.Name));
        var script = db.GetService<IMigrator>().GenerateScript("20261120043759_AddAuditEvents", MigrationId, MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("EXEC(N'CREATE TRIGGER [dbo].[TR_ExportLogs_NoChange]", script);
        Assert.DoesNotMatch(@"(?im)^\s*CREATE\s+TRIGGER", script);
        Assert.DoesNotMatch(@"(?i)\b(CREATE|DROP)\s+DATABASE\b|(?im)^\s*USE\s+", script);
        Assert.DoesNotContain("UPDATE [Users]", script);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_export_logs_cannot_be_edited_or_deleted(bool delete)
    {
        await using var db = TestSupport.InMemoryDb(Guid.NewGuid().ToString("N"));
        var log = new ExportLog { RunId = 1, JobId = 1, Stage = "all", Fit = "all", RowCount = 1, AuditEvent = new AuditEvent() };
        db.ExportLogs.Add(log); await db.SaveChangesAsync();
        if (delete) db.ExportLogs.Remove(log); else log.RowCount = 2;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
