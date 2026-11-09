using SmartCv.Core.Ai;
using SmartCv.Core.Constants;

namespace SmartCv.Core.Scoring;

/// <summary>Kết quả cuối cùng của một hồ sơ (A11/A12 ghi vào Evaluations + EvaluationSkills).</summary>
public sealed record CombinedScore(
    decimal KeywordScore,
    decimal? AiScore,
    decimal FinalScore,
    string ScoreSource,          // Combined | KeywordOnly
    string FitLabel,
    bool PredictedPass,
    decimal? YearsExperience,
    string Reasoning,
    IReadOnlyList<SkillMatch> Skills,
    string? ErrorMessage = null, // nguyên nhân AI lỗi; null nếu AI thành công
    bool IsSimulated = false);   // true: có dùng AI GIẢ, không được coi là Gemini đã đánh giá

/// <summary>Các mức đánh giá (tài liệu tổng quan mục 5).</summary>
public static class FitLevels
{
    public const string Excellent = "Rất phù hợp";   // từ 85
    public const string Good = "Phù hợp";            // từ 70
    public const string Potential = "Tiềm năng";     // từ 55
    public const string Poor = "Chưa phù hợp";       // dưới 55

    public static string FromScore(decimal score) => score switch
    {
        >= 85 => Excellent,
        >= 70 => Good,
        >= 55 => Potential,
        _ => Poor
    };
}

/// <summary>
/// Gộp điểm keyword (A9) và điểm AI (A10) thành điểm cuối.
///   AI thành công: Điểm cuối = KeywordWeight × keyword + AiWeight × AI      (ScoreSource = Combined)
///   AI lỗi       : Điểm cuối = keyword, AiScore = null, ghi rõ lý do         (ScoreSource = KeywordOnly, kết quả CHƯA ĐỦ AI)
///   Thiếu kỹ năng bắt buộc (cả keyword lẫn AI đều không thấy): điểm cuối không vượt MissingMustHaveCap.
/// Hàm thuần.
/// </summary>
public static class ScoreCombiner
{
    /// <summary>Ghi vào Evidence khi chỉ AI xác nhận: cột Evidence là chỗ duy nhất lưu được nguồn, và đây không phải trích dẫn từ CV.</summary>
    public const string AiConfirmedEvidence = "[Gemini xác nhận, không phải trích dẫn từ CV]";

    public static CombinedScore Combine(
        KeywordScoreResult keyword,
        AiEvaluationResult? ai,
        decimal keywordWeight,
        decimal aiWeight,
        decimal passThreshold,
        decimal? missingMustHaveCap)
    {
        if (keywordWeight < 0 || aiWeight < 0 || keywordWeight + aiWeight != 1m)
            throw new ArgumentException($"Trọng số không hợp lệ: keyword {keywordWeight} + AI {aiWeight} phải không âm và có tổng bằng 1.");

        var aiOk = ai is { IsSuccess: true, Score: not null };

        // Kỹ năng: có nếu keyword tìm thấy HOẶC AI xác nhận. Chỉ duyệt kỹ năng của job nên không thể sinh mã lạ.
        var aiMatched = aiOk
            ? ai!.MatchedSkillCodes.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skills = keyword.Skills
            .Select(s => s.IsMatched
                ? s with { MatchedBy = SkillMatchSources.Keyword }
                : aiMatched.Contains(s.SkillCode)
                    ? s with { IsMatched = true, Evidence = AiConfirmedEvidence, MatchedBy = SkillMatchSources.Ai }
                    : s)
            .ToList();

        decimal final;
        string source;
        if (aiOk)
        {
            final = Math.Round(keywordWeight * keyword.Score + aiWeight * ai!.Score!.Value, 2, MidpointRounding.AwayFromZero);
            source = ScoreSources.Combined;
        }
        else
        {
            final = keyword.Score;
            source = ScoreSources.KeywordOnly;
        }

        var errorMessage = aiOk ? null : ai?.ErrorMessage ?? "Không gọi được AI";
        var reasoning = aiOk ? ai!.Reasoning ?? string.Empty : $"{errorMessage}; điểm chỉ tính theo từ khóa.";

        var missingMust = skills.Where(s => s.IsMustHave && !s.IsMatched).Select(s => s.DisplayName).ToList();
        if (missingMust.Count > 0 && missingMustHaveCap is { } cap && final > cap)
        {
            final = cap;
            reasoning += $" (Điểm cuối giới hạn {cap:0} vì thiếu kỹ năng bắt buộc: {string.Join(", ", missingMust)}.)";
        }

        final = Math.Clamp(final, 0m, 100m);

        return new CombinedScore(
            KeywordScore: keyword.Score,
            AiScore: aiOk ? ai!.Score : null,
            FinalScore: final,
            ScoreSource: source,
            FitLabel: FitLevels.FromScore(final),
            PredictedPass: final >= passThreshold,
            YearsExperience: (aiOk ? ai!.YearsExperience : null) ?? keyword.YearsExperience,
            Reasoning: reasoning.Trim(),
            Skills: skills,
            ErrorMessage: errorMessage,
            IsSimulated: ai?.IsSimulated ?? false);
    }
}
