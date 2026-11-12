using System.Globalization;
using SmartCv.Core.Constants;

namespace SmartCv.Core.Scoring;

public sealed class ReviewFlagOptions
{
    public const string SectionName = "ReviewFlags";
    public decimal ScoreGap { get; set; } = 30m;
    public int MinTextLength { get; set; } = 500;
    public decimal CappedHighAiScore { get; set; } = 80m;

    public bool HasValidThresholds() => ScoreGap is > 0 and <= 100
        && MinTextLength > 0 && CappedHighAiScore is >= 0 and <= 100;
}

public static class ReviewFlagCodes
{
    public const string ScoreGap = "ScoreGap";
    public const string KeywordOnly = "KeywordOnly";
    public const string ShortText = "ShortText";
    public const string CappedHighAi = "CappedHighAi";
}

public sealed record ReviewFlag(string Code, string Message);

/// <summary>Stored results only. No CV text or candidate identity is needed.</summary>
public sealed record ReviewInput(string Status, decimal? KeywordScore, decimal? AiScore,
    decimal? FinalScore, string? ScoreSource, int? TextLength, decimal KeywordWeight,
    decimal AiWeight, bool HasSavedMissingMustHave, string? Reasoning);

public static class ReviewFlags
{
    public static List<ReviewFlag> Evaluate(ReviewInput input, ReviewFlagOptions options)
    {
        if (!options.HasValidThresholds()) throw new ArgumentException("Ngưỡng ReviewFlags không hợp lệ.", nameof(options));
        var flags = new List<ReviewFlag>();
        if (input.Status != EvaluationStatuses.Completed) return flags;

        var keywordOnly = input.KeywordScore is not null
            && (input.ScoreSource == ScoreSources.KeywordOnly || input.AiScore is null);
        if (keywordOnly)
            flags.Add(new(ReviewFlagCodes.KeywordOnly, "Kết quả chỉ dùng điểm từ khóa; chưa có đánh giá AI được sử dụng. HR nên đối chiếu CV gốc."));
        else if (input.KeywordScore is { } keyword && input.AiScore is { } ai && Math.Abs(keyword - ai) >= options.ScoreGap)
            flags.Add(new(ReviewFlagCodes.ScoreGap, "Điểm từ khóa và điểm AI chênh lệch lớn. HR nên đối chiếu hai kết quả với CV gốc."));

        if (input.TextLength is { } length && length < options.MinTextLength)
            flags.Add(new(ReviewFlagCodes.ShortText, $"Nội dung trích xuất dưới {options.MinTextLength.ToString(CultureInfo.InvariantCulture)} ký tự. Nên kiểm tra độ đầy đủ của văn bản từ CV gốc."));

        if (input.AiScore >= options.CappedHighAiScore && HasRecordedMissingSkillCap(input))
            flags.Add(new(ReviewFlagCodes.CappedHighAi, "AI chấm cao nhưng điểm cuối đã bị giới hạn theo quy tắc thiếu kỹ năng bắt buộc. HR nên kiểm tra kỹ năng và bằng chứng trong CV gốc."));
        return flags;
    }

    private static bool HasRecordedMissingSkillCap(ReviewInput input)
    {
        if (input.ScoreSource != ScoreSources.Combined || !input.HasSavedMissingMustHave
            || input.KeywordScore is not { } keyword || input.AiScore is not { } ai
            || input.FinalScore is not { } final || final is < 0 or > 100
            || input.KeywordWeight < 0 || input.AiWeight < 0 || input.KeywordWeight + input.AiWeight != 1)
            return false;

        // ScoreCombiner appends this audit marker ONLY when it actually applies the cap.
        // Check saved skill evidence and the rounded weighted score too. Never infer a cap
        // merely from FinalScore=69 or today's Scoring configuration (which may have changed).
        var marker = $"(Điểm cuối giới hạn {final.ToString("0", CultureInfo.InvariantCulture)} vì thiếu kỹ năng bắt buộc: ";
        return input.Reasoning?.Contains(marker, StringComparison.Ordinal) == true
            && Math.Round(keyword * input.KeywordWeight + ai * input.AiWeight, 2, MidpointRounding.AwayFromZero) > final;
    }
}
