namespace SmartCv.Core.Scoring;

/// <summary>Thông tin job cần cho việc chấm (lấy từ Jobs, JobSkills, JobSkillAliases).</summary>
public sealed record JobProfile(
    int JobId,
    string Title,
    string? Description,
    decimal? MinGpa,
    decimal MinYearsExperience,
    IReadOnlyList<SkillProfile> Skills);

public sealed record SkillProfile(string SkillCode, string DisplayName, string SkillType, IReadOnlyList<string> Aliases)
{
    public bool IsMustHave => SkillType == Constants.SkillTypes.MustHave;
}

/// <summary>Kết quả khớp một kỹ năng.</summary>
public sealed record SkillMatch(string SkillCode, string DisplayName, bool IsMustHave, bool IsMatched, string? Evidence, string? MatchedBy = null);

/// <summary>A10: nguồn xác nhận kỹ năng sau khi gộp điểm (SkillMatch.MatchedBy).</summary>
public static class SkillMatchSources
{
    public const string Keyword = "Keyword";
    public const string Ai = "AI";
}

/// <summary>Kết quả chấm keyword (A9).</summary>
public sealed record KeywordScoreResult(
    decimal Score,                          // 0–100
    IReadOnlyList<SkillMatch> Skills,
    decimal? Gpa,                           // thang 4; null nếu CV không ghi
    decimal? YearsExperience,               // null nếu không đọc được (KHÔNG phải 0)
    IReadOnlyList<string> Notes,            // giải thích ngắn: tiêu chí bị bỏ qua, ...
    bool HasApplicableCriteria = true)      // false: không có tiêu chí nào để chấm, Score = 0 không mang ý nghĩa
{
    public IEnumerable<string> MissingMustHaveCodes =>
        Skills.Where(s => s.IsMustHave && !s.IsMatched).Select(s => s.SkillCode);
}
