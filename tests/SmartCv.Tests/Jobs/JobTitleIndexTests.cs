using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Data.Migrations;

namespace SmartCv.Tests.Jobs;

public class JobTitleIndexTests
{
    internal const string PreviousMigration = "20261113014746_CvConsentRetention";
    internal const string MigrationId = "20261117042418_JobTitleUniqueIndex";
    private static SmartCvDbContext ModelContext() => new(new DbContextOptionsBuilder<SmartCvDbContext>()
        .UseSqlServer("Server=(local);Database=SmartCv_A20_ModelOnly;Integrated Security=True").Options);

    [Fact]
    public void Title_is_mutable_and_has_one_named_unique_index_while_JobId_remains_primary_key()
    {
        using var db = ModelContext();
        var job = db.Model.FindEntityType(typeof(Job))!;
        Assert.Equal(new[] { nameof(Job.JobId) }, job.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal("PK_Jobs", job.FindPrimaryKey()!.GetName());
        var title = job.FindProperty(nameof(Job.Title))!;
        Assert.Empty(title.GetContainingKeys());
        Assert.False(title.IsNullable);
        Assert.Equal(150, title.GetMaxLength());
        var index = Assert.Single(job.GetIndexes(), i => i.Properties.Contains(title));
        Assert.True(index.IsUnique);
        Assert.Equal("UQ_Jobs_Title", index.GetDatabaseName());
        Assert.Equal(new[] { nameof(Job.Title) }, index.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Named_defaults_checks_seed_and_foreign_keys_are_preserved()
    {
        using var db = ModelContext();
        var model = db.GetService<IDesignTimeModel>().Model;
        var job = model.FindEntityType(typeof(Job))!;
        Assert.Equal("DF_Jobs_IsActive", job.FindProperty(nameof(Job.IsActive))!.FindAnnotation("Relational:DefaultConstraintName")!.Value);
        Assert.Equal(true, job.FindProperty(nameof(Job.IsActive))!.GetDefaultValue());
        Assert.Equal("DF_Jobs_MinYearsExperience", job.FindProperty(nameof(Job.MinYearsExperience))!.FindAnnotation("Relational:DefaultConstraintName")!.Value);
        Assert.Equal(0m, job.FindProperty(nameof(Job.MinYearsExperience))!.GetDefaultValue());
        Assert.Equal("DF_Jobs_CreatedAt", job.FindProperty(nameof(Job.CreatedAt))!.FindAnnotation("Relational:DefaultConstraintName")!.Value);
        Assert.Equal("SYSUTCDATETIME()", job.FindProperty(nameof(Job.CreatedAt))!.GetDefaultValueSql());
        Assert.Equal(new[] { "CK_Jobs_Headcount", "CK_Jobs_MinGpa", "CK_Jobs_MinYearsExperience" }, job.GetCheckConstraints().Select(c => c.Name).Order());
        Assert.Equal(5, job.GetSeedData().Count());
        Assert.Equal(29, model.FindEntityType(typeof(JobSkill))!.GetSeedData().Count());
        Assert.Equal(52, model.FindEntityType(typeof(JobSkillAlias))!.GetSeedData().Count());
        var incoming = model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()).Where(fk => fk.PrincipalEntityType == job).ToArray();
        Assert.Equal(3, incoming.Length);   // Applications, JobSkills và (SCApp v2) EvaluationSets.JobId — ON DELETE SET NULL
        Assert.All(incoming, fk => Assert.Equal(new[] { nameof(Job.JobId) }, fk.PrincipalKey.Properties.Select(p => p.Name)));
    }

    [Fact]
    public void Migration_has_only_two_reversible_Title_operations_and_snapshot_matches_model()
    {
        var migration = new JobTitleUniqueIndex();
        Assert.Collection(migration.UpOperations,
            op => { var drop = Assert.IsType<DropUniqueConstraintOperation>(op); Assert.Equal("Jobs", drop.Table); Assert.Equal("UQ_Jobs_Title", drop.Name); },
            op => { var create = Assert.IsType<CreateIndexOperation>(op); Assert.Equal("Jobs", create.Table); Assert.Equal("UQ_Jobs_Title", create.Name); Assert.True(create.IsUnique); Assert.Equal(new[] { "Title" }, create.Columns); });
        Assert.Collection(migration.DownOperations,
            op => { var drop = Assert.IsType<DropIndexOperation>(op); Assert.Equal("Jobs", drop.Table); Assert.Equal("UQ_Jobs_Title", drop.Name); },
            op => { var create = Assert.IsType<AddUniqueConstraintOperation>(op); Assert.Equal("Jobs", create.Table); Assert.Equal("UQ_Jobs_Title", create.Name); Assert.Equal(new[] { "Title" }, create.Columns); });
        using var db = ModelContext();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(MigrationId, db.Database.GetMigrations().Take(5).Last());
        var target = migration.TargetModel.FindEntityType(typeof(Job))!;
        Assert.DoesNotContain(target.GetKeys(), key => key.Properties.Any(p => p.Name == nameof(Job.Title)));
        Assert.Equal("UQ_Jobs_Title", Assert.Single(target.GetIndexes()).GetDatabaseName());
    }

    [Fact]
    public async Task Change_tracking_can_rename_and_restore_a_tracked_Job_without_key_modification_errors()
    {
        await using var db = TestSupport.InMemoryDb("A20-" + Guid.NewGuid().ToString("N"));
        await db.Database.EnsureCreatedAsync();
        var job = await db.Jobs.OrderBy(j => j.JobId).FirstAsync();
        var original = job.Title;
        job.Title = "Vị trí thử A20";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        job = await db.Jobs.SingleAsync(j => j.Title == "Vị trí thử A20");
        job.Title = original;
        await db.SaveChangesAsync();
        Assert.Equal(5, await db.Jobs.CountAsync());
        Assert.Equal(original, await db.Jobs.AsNoTracking().Where(j => j.JobId == job.JobId).Select(j => j.Title).SingleAsync());
    }

    [Theory]
    [InlineData("SmartCvDb", "")]
    [InlineData("SmartCv_A19_Test_OptIn", "")]
    [InlineData("Other", "")]
    [InlineData("SmartCv_A20_Test_OptIn", "test.mdf")]
    public void SQL_opt_in_guard_refuses_application_other_task_and_attached_databases(string catalog, string attached)
    {
        var builder = new SqlConnectionStringBuilder { DataSource = ".", InitialCatalog = catalog, AttachDBFilename = attached };
        Assert.Throws<ArgumentException>(() => A20SqlFactAttribute.Validate(builder));
    }
}
