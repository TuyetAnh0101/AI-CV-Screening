using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Data.Migrations;

namespace SmartCv.Tests.Jobs;

public class JobCrudMigrationTests
{
    internal const string Previous = "20261117042418_JobTitleUniqueIndex";
    internal const string Current = "20261117045748_JobHeadcountClosingDate";

    [Fact]
    public void A21_migration_only_adds_two_nullable_columns_and_headcount_check_with_reversible_Down()
    {
        var migration = new JobHeadcountClosingDate();
        Assert.Equal(3, migration.UpOperations.Count);
        var columns = migration.UpOperations.OfType<AddColumnOperation>().ToDictionary(c => c.Name);
        Assert.Equal(2, columns.Count);
        Assert.Equal("date", columns["ClosingDate"].ColumnType);
        Assert.Equal("int", columns["Headcount"].ColumnType);
        Assert.All(columns.Values, c => { Assert.Equal("Jobs", c.Table); Assert.True(c.IsNullable); Assert.Null(c.DefaultValue); });
        var check = Assert.Single(migration.UpOperations.OfType<AddCheckConstraintOperation>());
        Assert.Equal("CK_Jobs_Headcount", check.Name);
        Assert.Contains("BETWEEN 1 AND 1000", check.Sql);
        Assert.Equal(3, migration.DownOperations.Count);
        Assert.IsType<DropCheckConstraintOperation>(migration.DownOperations[0]);
        Assert.Equal(new[] { "ClosingDate", "Headcount" }, migration.DownOperations.OfType<DropColumnOperation>().Select(c => c.Name).Order());
    }

    [Fact]
    public void A20_index_seed_named_defaults_and_current_snapshot_are_preserved()
    {
        using var db = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>()
            .UseSqlServer("Server=(local);Database=SmartCv_A21_ModelOnly;Integrated Security=True").Options);
        var job = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Job))!;
        Assert.True(job.FindProperty(nameof(Job.Headcount))!.IsNullable);
        Assert.True(job.FindProperty(nameof(Job.ClosingDate))!.IsNullable);
        Assert.Equal("date", job.FindProperty(nameof(Job.ClosingDate))!.GetColumnType());
        Assert.DoesNotContain(job.GetKeys(), k => k.Properties.Any(p => p.Name == nameof(Job.Title)));
        Assert.True(Assert.Single(job.GetIndexes(), i => i.GetDatabaseName() == "UQ_Jobs_Title").IsUnique);
        Assert.Equal(5, job.GetSeedData().Count());
        Assert.Equal("DF_Jobs_CreatedAt", job.FindProperty(nameof(Job.CreatedAt))!.FindAnnotation("Relational:DefaultConstraintName")!.Value);
        Assert.False(db.Database.HasPendingModelChanges());
        // A43 appends its journal migration; keep the exact A20/A21/A43 order checked.
        Assert.Equal(new[] { Previous, Current, "20261118131430_CandidateDataRequests" }, db.Database.GetMigrations().TakeLast(3));
    }
}
