using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartCv.Core.Ai;
using SmartCv.Core.Options;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Gemini;

namespace SmartCv.Infrastructure.Ai;

/// <summary>
/// AI GIẢ để thử luồng chấm mà không tốn hạn mức (Gemini:UseFake = true, chỉ ở Development).
/// Điểm AI = điểm keyword làm tròn, kỹ năng = kỹ năng keyword tìm thấy. Mọi kết quả có IsSimulated = true.
/// Giả lập lỗi: CV có chữ "#loi-ai" → trả lỗi vượt hạn mức, để thử nhánh KeywordOnly.
/// </summary>
public sealed class FakeAiEvaluator(IOptions<ScoringOptions> scoring, IOptions<GeminiOptions> gemini) : IAiEvaluator
{
    public const string ModelLabel = "fake-ai (giả lập)";
    public const string ErrorMarker = "#loi-ai";

    public async Task<AiEvaluationResult> EvaluateAsync(AiEvaluationRequest request, CancellationToken ct = default)
    {
        await Task.Delay(gemini.Value.FakeDelayMs, ct);

        if (request.CvText.Contains(ErrorMarker, StringComparison.OrdinalIgnoreCase))
            return AiEvaluationResult.Failure(AiErrorKind.QuotaExceeded, "Gemini vượt hạn mức sau 3 lần thử lại (giả lập)", 3) with { IsSimulated = true };

        var keyword = KeywordScoringService.Score(request.Job, request.CvText, scoring.Value, DateOnly.FromDateTime(DateTime.Today));
        var matched = keyword.Skills.Where(s => s.IsMatched).Select(s => s.SkillCode).ToList();
        var missing = keyword.Skills.Where(s => !s.IsMatched).Select(s => s.SkillCode).ToList();
        var reasoning = "(AI giả lập) " + (missing.Count == 0
            ? "CV đáp ứng đủ các kỹ năng của vị trí."
            : $"CV còn thiếu: {string.Join(", ", keyword.Skills.Where(s => !s.IsMatched).Select(s => s.DisplayName))}.");
        var score = Math.Round(keyword.Score, MidpointRounding.AwayFromZero);

        return new AiEvaluationResult
        {
            IsSuccess = true,
            IsSimulated = true,
            Score = score,
            MatchedSkillCodes = matched,
            MissingSkillCodes = missing,
            YearsExperience = keyword.YearsExperience,
            Gpa = keyword.Gpa,
            Reasoning = reasoning,
            RawResponse = JsonSerializer.Serialize(new
            {
                simulated = true,
                score,
                matchedSkills = matched,
                missingSkills = missing,
                yearsExperience = keyword.YearsExperience,
                gpa = keyword.Gpa,
                reasoning
            }),
            Attempts = 1
        };
    }
}
