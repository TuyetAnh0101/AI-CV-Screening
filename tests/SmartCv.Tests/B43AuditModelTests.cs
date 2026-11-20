using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SmartCv.Core.Auditing;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Data.Migrations;

namespace SmartCv.Tests;

public class B43AuditModelTests
{
    [Fact]
    public void Query_uses_bounded_Vietnam_calendar_dates_as_a_half_open_UTC_range()
    {
        var (query, errors) = AuditQuery.Parse(new Dictionary<string, string[]> { ["from"] = ["2026-11-20"], ["to"] = ["2026-11-20"] }, DateTime.UtcNow);
        Assert.Empty(errors); Assert.Equal(new DateTime(2026, 11, 19, 17, 0, 0, DateTimeKind.Utc), query!.FromUtc);
        Assert.Equal(query.FromUtc.AddDays(1), query.UntilUtc); Assert.Equal(25, query.PageSize);
    }

    [Theory]
    [InlineData("from", "2026-11-21", "to", "2026-11-20")]
    [InlineData("from", "2026-01-01", "to", "2026-12-31")]
    [InlineData("from", "0001-01-01", "to", "9999-12-31")]
    [InlineData("pageSize", "101", "page", "0")]
    [InlineData("actorUserId", "-1", "outcome", "arbitrary")]
    public void Invalid_query_values_cannot_reach_the_database(string key, string value, string second, string other)
    {
        var (query, errors) = AuditQuery.Parse(new Dictionary<string, string[]> { [key] = [value], [second] = [other] }, DateTime.UtcNow);
        Assert.Null(query); Assert.NotEmpty(errors);
    }

    [Fact]
    public void Audit_model_is_append_only_has_indexes_and_no_cascade_or_sensitive_columns()
    {
        using var db = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer("Server=never-connect.invalid;Database=SmartCv_B43_ModelOnly;Integrated Security=true").Options);
        var model = db.GetService<IDesignTimeModel>().Model;
        var audit = model.FindEntityType(typeof(AuditEvent))!;
        Assert.Equal("AuditEvents", audit.GetTableName());
        Assert.True(audit.FindProperty(nameof(AuditEvent.ActorUserId))!.IsNullable);
        Assert.Equal(DeleteBehavior.NoAction, Assert.Single(audit.GetForeignKeys()).DeleteBehavior);
        Assert.Equal(6, audit.GetIndexes().Count());
        Assert.Equal("SYSUTCDATETIME()", audit.FindProperty(nameof(AuditEvent.OccurredAtUtc))!.GetDefaultValueSql());
        Assert.Contains(audit.GetDeclaredTriggers(), t => t.ModelName == "TR_AuditEvents_NoChange");
        Assert.DoesNotContain(audit.GetProperties(), p => new[] { "Password", "PasswordHash", "SecurityStamp", "Token", "Email", "IpAddress", "Body", "Metadata" }.Contains(p.Name));
        var history = model.FindEntityType(typeof(ApplicationEvent))!;
        Assert.Contains(history.GetDeclaredTriggers(), t => t.ModelName == "TR_ApplicationEvents_NoChange");
        Assert.Contains(history.GetCheckConstraints(), c => c.Name == "CK_ApplicationEvents_Actor");
        Assert.All(history.GetCheckConstraints().Where(c => c.Name is "CK_ApplicationEvents_FromStage" or "CK_ApplicationEvents_ToStage" or "CK_ApplicationEvents_RejectReason"), c => Assert.Contains("Latin1_General_100_BIN2", c.Sql));
    }

    [Fact]
    public void Migration_Up_and_Down_only_affect_new_audit_structures()
    {
        var migration = new AddAuditEvents { ActiveProvider = "Microsoft.EntityFrameworkCore.SqlServer" };
        var table = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>()); Assert.Equal("AuditEvents", table.Name);
        Assert.Equal("Users", Assert.Single(table.ForeignKeys).PrincipalTable);
        Assert.All(migration.UpOperations.OfType<CreateIndexOperation>(), index => Assert.Equal("AuditEvents", index.Table));
        Assert.Single(migration.UpOperations.OfType<SqlOperation>(), sql => sql.Sql.Contains("CREATE TRIGGER [dbo].[TR_AuditEvents_NoChange]", StringComparison.Ordinal));
        Assert.DoesNotContain(migration.UpOperations, op => op is DropTableOperation or DropCheckConstraintOperation or AlterColumnOperation or DeleteDataOperation or UpdateDataOperation);
        Assert.DoesNotContain(migration.UpOperations.OfType<SqlOperation>(), sql => sql.Sql.Contains("ApplicationEvents", StringComparison.Ordinal) || sql.Sql.Contains("UPDATE Users", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("AuditEvents", Assert.IsType<DropTableOperation>(Assert.Single(migration.DownOperations)).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EF_refuses_updates_and_deletes_of_existing_audit_events(bool delete)
    {
        await using var db = TestSupport.InMemoryDb(Guid.NewGuid().ToString("N"));
        var row = new AuditEvent { OccurredAtUtc = DateTime.UtcNow, ActionCode = "auth.login", Category = "Authentication", ResourceType = "Session", Outcome = "Denied", RequestId = "synthetic", SafeSummary = "Đăng nhập" };
        db.AuditEvents.Add(row); await db.SaveChangesAsync();
        if (delete) db.AuditEvents.Remove(row); else row.SafeSummary = "overwrite";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
