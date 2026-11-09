namespace SmartCv.Core.Contracts;

/// <summary>A9: kết quả xem trước điểm keyword của một hồ sơ (GET /api/applications/{id}/keyword-score).</summary>
public sealed record KeywordPreviewDto(
    int ApplicationId, string FileName, string JobTitle, decimal KeywordScore,
    decimal? Gpa, decimal? YearsExperience, List<SkillPreviewDto> Skills, List<string> Notes);

public sealed record SkillPreviewDto(string SkillCode, string DisplayName, string SkillType, bool IsMatched, string? Evidence);
