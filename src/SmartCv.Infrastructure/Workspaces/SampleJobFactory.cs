using System.Text.Json;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data.Seed;

namespace SmartCv.Infrastructure.Workspaces;

public static class SampleJobFactory
{
    private sealed record SeedJob(
        int JobId,
        string Title,
        string? Description,
        decimal? MinGpa,
        decimal MinYearsExperience);

    private sealed record SeedSkill(
        int JobId,
        string SkillCode,
        string DisplayName,
        string SkillType);

    private sealed record SeedAlias(
        int JobId,
        string SkillCode,
        string Alias);

    private static readonly SeedJob Backend =
        JsonSerializer.Deserialize<SeedJob[]>(
            JsonSerializer.Serialize(JobSeedData.Jobs))!
        .Single(x => x.JobId == 1);

    private static readonly SeedSkill[] BackendSkills =
        JsonSerializer.Deserialize<SeedSkill[]>(
            JsonSerializer.Serialize(JobSeedData.JobSkills))!
        .Where(x => x.JobId == Backend.JobId)
        .ToArray();

    private static readonly SeedAlias[] BackendAliases =
        JsonSerializer.Deserialize<SeedAlias[]>(
            JsonSerializer.Serialize(JobSeedData.JobSkillAliases))!
        .Where(x => x.JobId == Backend.JobId)
        .ToArray();

    public static string Title => Backend.Title;

    public static Job Create(DateTime createdAtUtc) => new()
    {
        Title = Backend.Title,
        Description = Backend.Description,
        MinGpa = Backend.MinGpa,
        MinYearsExperience = Backend.MinYearsExperience,
        IsActive = true,
        CreatedAt = createdAtUtc,

        Skills = BackendSkills.Select(s => new JobSkill
        {
            SkillCode = s.SkillCode,
            DisplayName = s.DisplayName,
            SkillType = s.SkillType,

            Aliases = BackendAliases
                .Where(a => a.SkillCode == s.SkillCode)
                .Select(a => new JobSkillAlias
                {
                    Alias = a.Alias
                })
                .ToList()
        }).ToList()
    };
}