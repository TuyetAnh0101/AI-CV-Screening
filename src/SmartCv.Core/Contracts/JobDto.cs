using System.Text.Json.Serialization;

namespace SmartCv.Core.Contracts;

// Dữ liệu job mà API GET /api/jobs trả về. Dùng chung cho API (bạn A) và giao diện (bạn B).

public sealed class JobDto
{
    public int JobId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public decimal? MinGpa { get; init; }
    public decimal MinYearsExperience { get; init; }
    public bool IsActive { get; init; }
    public List<JobSkillDto> Skills { get; init; } = [];
    public int? Headcount { get; init; }
    public DateOnly? ClosingDate { get; init; }
    public bool IsExpired { get; init; }
    public DateTime CreatedAt { get; init; }
    public int MustHaveCount { get; init; }

    // Internal HR statistics are omitted from the existing public GET outside the HR access gate.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasEvaluations { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ApplicationCount { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, int>? StageCounts { get; init; }
}

public sealed class JobSkillDto
{
    public string SkillCode { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string SkillType { get; init; } = string.Empty;   // MustHave | NiceToHave
    public List<string> Aliases { get; init; } = [];
}
