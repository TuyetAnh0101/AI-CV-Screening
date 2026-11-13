using SmartCv.Core.Constants;

namespace SmartCv.Core.Evaluation;

/// <summary>A current HR label and the persisted prediction for one application in one run.</summary>
public sealed record LabeledPrediction(
    int ApplicationId,
    bool HrIsPass,
    string Status,
    bool? PredictedPass,
    string? ScoreSource = null);

public enum PredictionOutcome
{
    Correct,
    Missed,
    WrongPick,
    Unscored
}

public static class MismatchTypes
{
    public const string Missed = "Missed";
    public const string WrongPick = "WrongPick";
}

public sealed record AccuracyResult(
    int Total,
    int Correct,
    int Missed,
    int WrongPick,
    int Unscored,
    int KeywordOnly,
    decimal? AccuracyPercent)
{
    public int Scored => Total - Unscored;

    /// <summary>A41: HR Đạt, AI Đạt. TruePass + TrueFail = Correct.</summary>
    public int TruePass { get; init; }

    /// <summary>A41: HR Không đạt, AI Không đạt.</summary>
    public int TrueFail { get; init; }

    /// <summary>A41: khoảng tin cậy 95% (Wilson) của tỷ lệ đúng Correct/Total, đơn vị %. null khi không có CV có nhãn.</summary>
    public (decimal Low, decimal High)? Confidence95 =>
        Total == 0 ? null : PromptAccuracyCalculator.Wilson(Correct, Total);
}

/// <summary>
/// A16's single accuracy formula. Every labeled input remains in the denominator,
/// including failed, pending and missing predictions. No labels means no percentage.
/// The decision comes from PredictedPass; neither scores nor the prompt are inputs.
/// </summary>
public static class PromptAccuracyCalculator
{
    public static PredictionOutcome Classify(bool hrIsPass, string status, bool? predictedPass)
    {
        if (status != EvaluationStatuses.Completed || predictedPass is null) return PredictionOutcome.Unscored;
        if (hrIsPass == predictedPass.Value) return PredictionOutcome.Correct;
        return hrIsPass ? PredictionOutcome.Missed : PredictionOutcome.WrongPick;
    }

    public static PredictionOutcome Classify(LabeledPrediction prediction) =>
        Classify(prediction.HrIsPass, prediction.Status, prediction.PredictedPass);

    public static bool? IsMatch(PredictionOutcome outcome) => outcome switch
    {
        PredictionOutcome.Correct => true,
        PredictionOutcome.Missed or PredictionOutcome.WrongPick => false,
        _ => null
    };

    public static string? MismatchType(PredictionOutcome outcome) => outcome switch
    {
        PredictionOutcome.Missed => MismatchTypes.Missed,
        PredictionOutcome.WrongPick => MismatchTypes.WrongPick,
        _ => null
    };

    public static AccuracyResult Calculate(IEnumerable<LabeledPrediction> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        int total = 0, correct = 0, missed = 0, wrongPick = 0, unscored = 0, keywordOnly = 0, truePass = 0, trueFail = 0;
        foreach (var prediction in items)
        {
            total++;
            switch (Classify(prediction))
            {
                case PredictionOutcome.Correct:
                    correct++;
                    if (prediction.HrIsPass) truePass++; else trueFail++;
                    break;
                case PredictionOutcome.Missed: missed++; break;
                case PredictionOutcome.WrongPick: wrongPick++; break;
                default: unscored++; break;
            }
            if (prediction.Status == EvaluationStatuses.Completed && prediction.ScoreSource == ScoreSources.KeywordOnly)
                keywordOnly++;
        }
        decimal? accuracy = total == 0
            ? null
            : Math.Round(correct * 100m / total, 2, MidpointRounding.AwayFromZero);
        return new(total, correct, missed, wrongPick, unscored, keywordOnly, accuracy) { TruePass = truePass, TrueFail = trueFail };
    }

    /// <summary>
    /// A41: khoảng tin cậy Wilson cho tỷ lệ <paramref name="successes"/>/<paramref name="n"/>, đơn vị %, làm tròn 2 chữ số.
    ///   p̂ = k/n ; tâm = (p̂ + z²/2n) / (1 + z²/n) ; nửa độ rộng = z·√(p̂(1−p̂)/n + z²/4n²) / (1 + z²/n)
    /// z = 1,96 (95%). Không dùng "p̂ ± 1,96·√(p̂(1−p̂)/n)" (Wald): Wald vượt 0–100% hoặc có độ rộng 0 khi đúng hết, sai nhiều khi n nhỏ.
    /// Ví dụ 35/50 → 56,25% – 80,90%; 10/10 → 72,25% – 100%. n = 0 không có khoảng (ném lỗi; AccuracyResult trả null).
    /// Đây là mức độ chắc chắn của tỷ lệ đúng trên bộ CV đã có, không phải kiểm định thống kê giữa hai prompt.
    /// </summary>
    public static (decimal Low, decimal High) Wilson(int successes, int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(successes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(successes, n);

        const double z = 1.96;
        const double z2 = z * z;
        var p = (double)successes / n;
        var denominator = 1 + z2 / n;
        var center = (p + z2 / (2.0 * n)) / denominator;
        var half = z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n)) / denominator;

        // Sai số làm tròn số thực có thể cho -0,0000001 hoặc 1,0000001: kẹp về 0–1 trước khi đổi ra %
        static decimal Percent(double value) =>
            Math.Round((decimal)Math.Clamp(value, 0, 1) * 100m, 2, MidpointRounding.AwayFromZero);
        return (Percent(center - half), Percent(center + half));
    }
}
