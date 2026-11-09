namespace SmartCv.Core.Contracts;

/// <summary>
/// A10: kết quả chấm thử (POST /api/applications/{id}/ai-preview). Không có JSON gốc của AI vì API chưa có xác thực.
/// ScoreSource = KeywordOnly nghĩa là AI lỗi, điểm CHƯA có phần 70% AI. AiSimulated = true nghĩa là dùng AI giả.
/// </summary>
public sealed record AiPreviewDto(
    string PromptVersion, string Model, decimal KeywordScore, decimal? AiScore, decimal FinalScore,
    string ScoreSource, string FitLabel, bool PredictedPass, decimal? YearsExperience, string Reasoning,
    bool AiSucceeded, string AiErrorKind, string? AiErrorMessage, int AiAttempts, bool AiSimulated, bool AiCvTruncated,
    List<string> AiMatchedSkills, List<string> AiMissingSkills, List<string> AiInvalidSkillCodes,
    List<AiPreviewSkillDto> Skills);

/// <summary>MatchedBy: Keyword (Evidence là trích dẫn từ CV), AI (chỉ Gemini xác nhận), null (không tìm thấy).</summary>
public sealed record AiPreviewSkillDto(string SkillCode, string DisplayName, string SkillType, bool IsMatched, string? MatchedBy, string? Evidence);
