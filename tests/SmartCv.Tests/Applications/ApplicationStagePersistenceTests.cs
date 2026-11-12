using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Applications;

public class ApplicationStagePersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_contexts_read_before_either_saves_losing_save_is_rejected(bool changeReasonOnly)
    {
        var name = Guid.NewGuid().ToString("N");
        await using var first = TestSupport.InMemoryDb(name);
        await first.Database.EnsureCreatedAsync();
        var app = ApplicationStageEndpointTests.NewApplication();
        if (changeReasonOnly) { app.Stage = "Rejected"; app.RejectReason = "MissingSkills"; }
        first.Add(app);
        await first.SaveChangesAsync();
        await using var second = TestSupport.InMemoryDb(name);
        var stale = await second.Applications.SingleAsync();
        app.Stage = changeReasonOnly ? "Rejected" : "Shortlisted";
        app.RejectReason = changeReasonOnly ? "Duplicate" : null;
        await first.SaveChangesAsync();
        stale.Stage = "Interview";
        stale.RejectReason = null;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        await using var verify = TestSupport.InMemoryDb(name);
        var saved = await verify.Applications.SingleAsync();
        Assert.Equal(app.Stage, saved.Stage);
        Assert.Equal(app.RejectReason, saved.RejectReason);
        Assert.NotEqual(stale.StageVersion, saved.StageVersion);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Tracked_history_cannot_be_modified_or_deleted(bool delete, bool synchronous)
    {
        await using var db = TestSupport.InMemoryDb(Guid.NewGuid().ToString("N"));
        await db.Database.EnsureCreatedAsync();
        var app = ApplicationStageEndpointTests.NewApplication();
        var ev = new ApplicationEvent { Application = app, FromStage = "New", ToStage = "Reviewing", Actor = "HR", CreatedAt = DateTime.UtcNow };
        db.Add(ev);
        await db.SaveChangesAsync();
        if (delete) db.Remove(ev); else ev.Note = "Rewrite history";
        if (synchronous) Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        else await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void Migration_contains_only_additive_stage_schema_checks_trigger_and_safe_output_configuration()
    {
        using var db = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>()
            .UseSqlServer("Server=localhost;Database=SmartCv_A35_DesignOnly;Integrated Security=True;TrustServerCertificate=True").Options);
        var script = db.GetService<IMigrator>().GenerateScript("20261106142155_SeedJobs", "ApplicationStages");
        Assert.Contains("ADD [Stage] nvarchar(20) NOT NULL DEFAULT N'New'", script);
        Assert.Contains("ADD [StageVersion] uniqueidentifier NOT NULL DEFAULT (NEWID())", script);
        Assert.Contains("CREATE TABLE [ApplicationEvents]", script);
        Assert.Contains("TR_ApplicationEvents_NoChange", script);
        Assert.Contains("INSTEAD OF UPDATE, DELETE", script);
        Assert.Contains("THROW 51000", script);
        Assert.DoesNotContain("DROP TABLE", script);
        Assert.DoesNotContain("DELETE FROM", script);
        Assert.DoesNotContain("DISABLE TRIGGER", script);
        Assert.Contains("CK_Applications_RejectReason_Stage", script);
        Assert.Contains("CK_ApplicationEvents_Actor", script);
        var entity = db.Model.FindEntityType(typeof(Application))!;
        Assert.All(new[] { "StageVersion", "Stage", "RejectReason" }, name => Assert.True(entity.FindProperty(name)!.IsConcurrencyToken));
        Assert.False(db.Model.FindEntityType(typeof(ApplicationEvent))!.IsSqlOutputClauseUsed());
        Assert.Equal(DeleteBehavior.Restrict, db.Model.FindEntityType(typeof(ApplicationEvent))!.GetForeignKeys().Single().DeleteBehavior);
    }
}
