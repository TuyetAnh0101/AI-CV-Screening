using SmartCv.Core.Constants;
using SmartCv.Core.Evaluation;

namespace SmartCv.Tests;

/// <summary>
/// A41: khoảng tin cậy 95% Wilson, ma trận 2×2 và mức độ kết luận khi so sánh. Hàm thuần, không database.
/// Số tính tay theo hướng dẫn A41: 35/50 → 56,25–80,90%; 7/10 → 39,68–89,22%; 8/10 → 49,02–94,33%; 10/10 → 72,25–100%.
/// </summary>
public class WilsonIntervalTests
{
    private static readonly RunSettings Settings = new(1, "gemini-2.5-flash", 0.20m, 0.30m, 0.70m, 70m);

    [Theory]
    [InlineData(35, 50, "56.25", "80.90")]
    [InlineData(7, 10, "39.68", "89.22")]
    [InlineData(8, 10, "49.02", "94.33")]
    [InlineData(10, 10, "72.25", "100")]
    [InlineData(0, 10, "0", "27.75")]
    [InlineData(1, 1, "20.65", "100")]
    public void Wilson_matches_hand_calculation(int correct, int n, string low, string high)
    {
        var (l, h) = PromptAccuracyCalculator.Wilson(correct, n);

        Assert.Equal(decimal.Parse(low, System.Globalization.CultureInfo.InvariantCulture), l);
        Assert.Equal(decimal.Parse(high, System.Globalization.CultureInfo.InvariantCulture), h);
    }

    [Fact]
    public void Interval_always_stays_within_0_100_and_contains_the_observed_rate()
    {
        for (var n = 1; n <= 80; n++)
            for (var k = 0; k <= n; k++)
            {
                var (low, high) = PromptAccuracyCalculator.Wilson(k, n);
                var rate = Math.Round(k * 100m / n, 2, MidpointRounding.AwayFromZero);
                Assert.InRange(low, 0m, 100m);
                Assert.InRange(high, 0m, 100m);
                Assert.True(low <= rate && rate <= high, $"{k}/{n}: {low}–{high} không chứa {rate}");
                Assert.Equal(decimal.Round(low, 2), low);
                Assert.Equal(decimal.Round(high, 2), high);
            }
    }

    [Fact]
    public void Wilson_is_not_the_wald_interval_at_the_edges()
    {
        // Wald: 10/10 → 100–100 (độ rộng 0), 1/10 → âm. Wilson luôn có độ rộng và nằm trong 0–100.
        var (allLow, allHigh) = PromptAccuracyCalculator.Wilson(10, 10);
        var (oneLow, _) = PromptAccuracyCalculator.Wilson(1, 10);

        Assert.True(allLow < allHigh);
        Assert.True(oneLow > 0m);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(-1, 10)]
    [InlineData(11, 10)]
    public void Invalid_counts_throw_instead_of_returning_fake_numbers(int correct, int n)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PromptAccuracyCalculator.Wilson(correct, n));
    }

    [Fact]
    public void No_labeled_cv_means_no_percentage_and_no_interval()
    {
        var result = PromptAccuracyCalculator.Calculate([]);

        Assert.Null(result.AccuracyPercent);
        Assert.Null(result.Confidence95);
        Assert.Equal((0, 0), (result.TruePass, result.TrueFail));
    }

    [Fact]
    public void Matrix_cells_add_up_to_all_labeled_cvs_with_mixed_statuses()
    {
        LabeledPrediction P(int id, bool hr, string status, bool? ai, string? source = ScoreSources.Combined) => new(id, hr, status, ai, source);
        var result = PromptAccuracyCalculator.Calculate(
        [
            P(1, true, EvaluationStatuses.Completed, true),
            P(2, true, EvaluationStatuses.Completed, true, ScoreSources.KeywordOnly),    // KeywordOnly vẫn có dự đoán → vào ô
            P(3, true, EvaluationStatuses.Completed, false),
            P(4, false, EvaluationStatuses.Completed, true),
            P(5, false, EvaluationStatuses.Completed, false),
            P(6, false, EvaluationStatuses.Completed, false, ScoreSources.KeywordOnly),
            P(7, true, EvaluationStatuses.Failed, null, null),
            P(8, false, EvaluationStatuses.Pending, null, null),
            P(9, true, EvaluationStatuses.Completed, null)                                 // chấm xong nhưng không có dự đoán
        ]);

        Assert.Equal((2, 1, 1, 2, 3), (result.TruePass, result.Missed, result.WrongPick, result.TrueFail, result.Unscored));
        Assert.Equal(result.Total, result.TruePass + result.Missed + result.WrongPick + result.TrueFail + result.Unscored);
        Assert.Equal(result.Correct, result.TruePass + result.TrueFail);
        Assert.Equal(44.44m, result.AccuracyPercent);   // A16 giữ nguyên: 4/9, CV không chấm được vẫn ở mẫu số
        Assert.Equal(PromptAccuracyCalculator.Wilson(4, 9), result.Confidence95);
        Assert.Equal(2, result.KeywordOnly);
    }

    // ---------- Mức độ kết luận khi so sánh (A17 + A41) ----------

    private static CompareRun Run(int runId, bool[] hr, bool[] ai, RunSettings? settings = null, string status = RunStatuses.Completed) =>
        new(runId, $"v{runId}", status, settings ?? Settings,
            hr.Select((h, i) => new LabeledPrediction(i + 1, h, EvaluationStatuses.Completed, ai[i], ScoreSources.Combined)).ToList());

    private static bool[] Predictions(bool[] hr, int correct) => hr.Select((h, i) => i < correct ? h : !h).ToArray();

    [Fact]
    public void Demo_v1_v2_overlap_is_inconclusive_while_verdict_and_summary_stay_unchanged()
    {
        var hr = PromptCompareSeed.Hr;
        var v1 = PromptCompareSeed.V1Scores.Select(s => s >= 70m).ToArray();
        var v2 = PromptCompareSeed.V2Scores.Select(s => s >= 70m).ToArray();

        var c = PromptComparer.Compare(Run(1, hr, v1), Run(2, hr, v2));

        Assert.Equal(CompareVerdicts.Better, c.Verdict);   // B16 không phải sửa
        Assert.Equal(
            "v2 đúng 80% (8/10 CV), cao hơn v1 10 điểm phần trăm (70%, 7/10 CV). " +
            "Sửa đúng thêm 1 CV, không làm sai thêm CV nào. Bỏ sót giữ nguyên (1 → 1), chọn nhầm giảm 1 (2 → 1).", c.Summary);
        Assert.False(c.Conclusive);
        Assert.StartsWith(PromptComparer.InconclusiveText, c.ConclusionNote);
        Assert.Contains("chỉ là kết quả quan sát, chưa đủ cơ sở kết luận", c.ConclusionNote);
        Assert.Contains("39,68–89,22%", c.ConclusionNote);
        Assert.Contains("49,02–94,33%", c.ConclusionNote);
        Assert.Equal((5, 1, 2, 2), (c.Base.TruePass, c.Base.Missed, c.Base.WrongPick, c.Base.TrueFail));
        Assert.Equal((5, 1, 1, 3), (c.Candidate.TruePass, c.Candidate.Missed, c.Candidate.WrongPick, c.Candidate.TrueFail));
    }

    [Fact]
    public void Non_overlapping_intervals_are_conclusive_but_never_called_certain()
    {
        var hr = Enumerable.Range(0, 50).Select(i => i % 2 == 0).ToArray();

        var c = PromptComparer.Compare(Run(1, hr, Predictions(hr, 10)), Run(2, hr, Predictions(hr, 45)));

        Assert.Equal(CompareVerdicts.Better, c.Verdict);
        Assert.True(c.Conclusive);
        Assert.Contains("không chồng nhau", c.ConclusionNote);
        Assert.Contains("không phải kiểm định thống kê chính thức", c.ConclusionNote);
        Assert.DoesNotContain("chắc chắn", c.ConclusionNote);
    }

    [Fact]
    public void Worse_with_clear_gap_is_also_conclusive()
    {
        var hr = Enumerable.Range(0, 50).Select(i => i % 2 == 0).ToArray();

        var c = PromptComparer.Compare(Run(1, hr, Predictions(hr, 45)), Run(2, hr, Predictions(hr, 10)));

        Assert.Equal(CompareVerdicts.Worse, c.Verdict);
        Assert.True(c.Conclusive);
    }

    [Fact]
    public void Same_accuracy_is_inconclusive_with_the_required_sentence()
    {
        var hr = PromptCompareSeed.Hr;
        var ai = PromptCompareSeed.V1Scores.Select(s => s >= 70m).ToArray();

        var c = PromptComparer.Compare(Run(1, hr, ai), Run(2, hr, ai));

        Assert.Equal(CompareVerdicts.Same, c.Verdict);
        Assert.False(c.Conclusive);
        Assert.Equal(PromptComparer.InconclusiveText, c.ConclusionNote);
    }

    [Fact]
    public void Not_comparable_runs_are_never_conclusive()
    {
        var hr = Enumerable.Range(0, 50).Select(i => i % 2 == 0).ToArray();
        var otherModel = Settings with { ModelName = "gemini-other" };

        var c = PromptComparer.Compare(Run(1, hr, Predictions(hr, 10)), Run(2, hr, Predictions(hr, 45), otherModel));

        Assert.Equal(CompareVerdicts.NotComparable, c.Verdict);
        Assert.False(c.Conclusive);
        Assert.StartsWith("Chưa thể kết luận", c.ConclusionNote);
    }

    [Fact]
    public void Unfinished_run_is_never_conclusive()
    {
        var hr = Enumerable.Range(0, 50).Select(i => i % 2 == 0).ToArray();

        var c = PromptComparer.Compare(Run(1, hr, Predictions(hr, 10)), Run(2, hr, Predictions(hr, 45), status: RunStatuses.Running));

        Assert.Equal(CompareVerdicts.NotComparable, c.Verdict);
        Assert.False(c.Conclusive);
    }

    [Fact]
    public void Touching_intervals_count_as_overlap()
    {
        Assert.False(PromptComparer.IsConclusive((10m, 20m), (20m, 30m)));
        Assert.True(PromptComparer.IsConclusive((10m, 19.99m), (20m, 30m)));
        Assert.True(PromptComparer.IsConclusive((40m, 50m), (10m, 39.99m)));
        Assert.False(PromptComparer.IsConclusive(null, (20m, 30m)));
        Assert.False(PromptComparer.IsConclusive((20m, 30m), null));
    }
}
