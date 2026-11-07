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
}

public sealed class JobSkillDto
{
    public string SkillCode { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string SkillType { get; init; } = string.Empty;   // MustHave | NiceToHave
    public List<string> Aliases { get; init; } = [];
}
