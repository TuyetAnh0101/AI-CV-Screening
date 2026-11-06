using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

/// <summary>
/// A3: kiểm tra dữ liệu nền (HasData) và migration SeedJobs.
/// Chỉ dựng EF model, không mở kết nối tới SQL Server.
/// </summary>
public class JobSeedDataTests
{
    private static SmartCvDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<SmartCvDbContext>()
            .UseSqlServer("Server=(local);Database=SmartCvDb_ModelOnly;Trusted_Connection=True")
            .Options);

    // HasData chỉ có trong design-time model (model dùng khi tạo migration), không có trong runtime model.
    private static List<IDictionary<string, object?>> Seed<T>(SmartCvDbContext db) =>
        db.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(T))!.GetSeedData()
            .Select(d => (IDictionary<string, object?>)d.ToDictionary()).ToList();

    [Fact]
    public void Seed_has_5_jobs_29_skills_52_aliases()
    {
        using var db = CreateContext();
        Assert.Equal(5, Seed<Job>(db).Count);
        Assert.Equal(29, Seed<JobSkill>(db).Count);
        Assert.Equal(52, Seed<JobSkillAlias>(db).Count);
    }

    [Fact]
    public void Seed_keys_are_unique_and_relationships_are_valid()
    {
        using var db = CreateContext();
        var jobs = Seed<Job>(db);
        var skills = Seed<JobSkill>(db);
        var aliases = Seed<JobSkillAlias>(db);

        var jobIds = jobs.Select(j => (int)j["JobId"]!).ToList();
        Assert.Equal(jobIds.Count, jobIds.Distinct().Count());
        Assert.Equal(jobs.Count, jobs.Select(j => (string)j["Title"]!).Distinct().Count());

        var skillKeys = skills.Select(s => ((int)s["JobId"]!, (string)s["SkillCode"]!)).ToList();
        Assert.Equal(skillKeys.Count, skillKeys.Distinct().Count());
        Assert.All(skillKeys, k => Assert.Contains(k.Item1, jobIds));

        var aliasKeys = aliases.Select(a => ((int)a["JobId"]!, (string)a["SkillCode"]!, (string)a["Alias"]!)).ToList();
        Assert.Equal(aliasKeys.Count, aliasKeys.Distinct().Count());
        Assert.All(aliasKeys, a => Assert.Contains((a.Item1, a.Item2), skillKeys));

        // Kỹ năng nào cũng có ít nhất một từ khóa tương đương
        Assert.All(skillKeys, k => Assert.Contains(aliasKeys, a => a.Item1 == k.Item1 && a.Item2 == k.Item2));
    }

    [Theory]
    [InlineData(1, 3, 3, 11)]
    [InlineData(2, 3, 3, 11)]
    [InlineData(3, 3, 3, 9)]
    [InlineData(4, 3, 2, 9)]
    [InlineData(5, 3, 3, 12)]
    public void Each_job_has_expected_skills_and_aliases(int jobId, int mustHave, int niceToHave, int aliasCount)
    {
        using var db = CreateContext();
        var skills = Seed<JobSkill>(db).Where(s => (int)s["JobId"]! == jobId).ToList();

        Assert.Equal(mustHave, skills.Count(s => (string)s["SkillType"]! == SkillTypes.MustHave));
        Assert.Equal(niceToHave, skills.Count(s => (string)s["SkillType"]! == SkillTypes.NiceToHave));
        Assert.Equal(aliasCount, Seed<JobSkillAlias>(db).Count(a => (int)a["JobId"]! == jobId));
    }

    [Fact]
    public void Jobs_use_fixed_seed_date_and_keep_unicode()
    {
        using var db = CreateContext();
        var expected = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.All(Seed<Job>(db), j => Assert.Equal(expected, (DateTime)j["CreatedAt"]!));

        var skills = Seed<JobSkill>(db);
        Assert.Contains(skills, s => (string)s["DisplayName"]! == "Kiểm thử thủ công");
        Assert.Contains(Seed<JobSkillAlias>(db), a => (string)a["Alias"]! == "Xử lý ngôn ngữ tự nhiên");
    }

    [Fact]
    public void Migrations_are_InitialCreate_then_SeedJobs_and_model_has_no_pending_changes()
    {
        using var db = CreateContext();
        Assert.Equal(
            new[] { "20261106132922_InitialCreate", "20261106142155_SeedJobs" },
            db.Database.GetMigrations().ToArray());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
