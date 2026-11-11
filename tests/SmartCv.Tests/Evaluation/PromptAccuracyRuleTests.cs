using SmartCv.Core.Constants;
using SmartCv.Core.Evaluation;

// Thư mục Evaluation/ nhưng giữ namespace gốc: namespace SmartCv.Tests.Evaluation sẽ che entity Evaluation
// mà nhiều test khác đang dùng (lỗi CS0118).
namespace SmartCv.Tests;

/// <summary>
/// A18.3: công thức A16 (PromptAccuracyCalculator): Accuracy = Correct / TotalLabeled × 100, làm tròn 2 chữ số.
/// Missed = HR Đạt, AI Không đạt; WrongPick = HR Không đạt, AI Đạt; Unscored (Pending/Failed/không có dự đoán)
/// vẫn nằm trong mẫu số. Bổ sung cho PromptAccuracyCalculatorTests (A16).
/// </summary>
public class PromptAccuracyRuleTests
{
    /// <summary>Dữ liệu demo theo đúng thứ tự hồ sơ 1..10 của seed_demo.sql; dự đoán Đạt = điểm cuối ≥ 70 (A10).</summary>
    private static List<LabeledPrediction> DemoRun(IReadOnlyList<decimal> finalScores) =>
        finalScores.Select((score, i) => new LabeledPrediction(
            i + 1, PromptCompareSeed.Hr[i], EvaluationStatuses.Completed, score >= 70m, ScoreSources.Combined)).ToList();

    private static int[] ApplicationsWith(IEnumerable<LabeledPrediction> rows, PredictionOutcome outcome) =>
        rows.Where(r => PromptAccuracyCalculator.Classify(r) == outcome).Select(r => r.ApplicationId).ToArray();

    [Fact]
    public void Du_lieu_demo_v1_dung_7_tren_10_bo_sot_ho_so_2_chon_nham_ho_so_5_va_9()
    {
        var rows = DemoRun(PromptCompareSeed.V1Scores);

        var r = PromptAccuracyCalculator.Calculate(rows);

        Assert.Equal((10, 7, 1, 2, 0, 0), (r.Total, r.Correct, r.Missed, r.WrongPick, r.Unscored, r.KeywordOnly));
        Assert.Equal(70.00m, r.AccuracyPercent);
        Assert.Equal([2], ApplicationsWith(rows, PredictionOutcome.Missed));
        Assert.Equal([5, 9], ApplicationsWith(rows, PredictionOutcome.WrongPick));
    }

    [Fact]
    public void Du_lieu_demo_v2_dung_8_tren_10_bo_sot_ho_so_2_chon_nham_ho_so_5()
    {
        var rows = DemoRun(PromptCompareSeed.V2Scores);

        var r = PromptAccuracyCalculator.Calculate(rows);

        Assert.Equal((10, 8, 1, 1, 0), (r.Total, r.Correct, r.Missed, r.WrongPick, r.Unscored));
        Assert.Equal(80.00m, r.AccuracyPercent);
        Assert.Equal([2], ApplicationsWith(rows, PredictionOutcome.Missed));
        Assert.Equal([5], ApplicationsWith(rows, PredictionOutcome.WrongPick));
    }

    [Theory]
    [InlineData(true, 100, 0, 0)]
    [InlineData(false, 0, 2, 2)]
    public void Tat_ca_dung_la_100_phan_tram_tat_ca_sai_la_0_phan_tram(bool allCorrect, int expected, int missed, int wrongPick)
    {
        bool[] hr = [true, false, true, false];
        var rows = hr.Select((label, i) => new LabeledPrediction(i + 1, label, EvaluationStatuses.Completed, allCorrect ? label : !label));

        var r = PromptAccuracyCalculator.Calculate(rows);

        Assert.Equal((decimal)expected, r.AccuracyPercent);
        Assert.Equal((missed, wrongPick, 0), (r.Missed, r.WrongPick, r.Unscored));
    }

    [Fact]
    public void Ty_le_dung_luon_bang_Correct_chia_TotalLabeled_tren_nhieu_bo_du_lieu_ngau_nhien_co_dinh()
    {
        var random = new Random(20261111);   // hạt giống cố định: lần chạy nào cũng cùng dữ liệu
        string[] statuses = [EvaluationStatuses.Completed, EvaluationStatuses.Completed, EvaluationStatuses.Completed,
            EvaluationStatuses.Pending, EvaluationStatuses.Failed];

        for (var round = 0; round < 300; round++)
        {
            var rows = Enumerable.Range(1, random.Next(1, 40)).Select(id => new LabeledPrediction(
                id,
                random.Next(2) == 0,
                statuses[random.Next(statuses.Length)],
                random.Next(4) switch { 0 => null, 1 => true, _ => false },
                random.Next(3) == 0 ? ScoreSources.KeywordOnly : ScoreSources.Combined)).ToList();

            // Định nghĩa nghiệp vụ, tính độc lập với PromptAccuracyCalculator
            var scored = rows.Where(x => x.Status == EvaluationStatuses.Completed && x.PredictedPass is not null).ToList();
            var correct = scored.Count(x => x.PredictedPass == x.HrIsPass);
            var missed = scored.Count(x => x.HrIsPass && x.PredictedPass == false);
            var wrongPick = scored.Count(x => !x.HrIsPass && x.PredictedPass == true);
            var keywordOnly = rows.Count(x => x.Status == EvaluationStatuses.Completed && x.ScoreSource == ScoreSources.KeywordOnly);
            var expected = (rows.Count, correct, missed, wrongPick, rows.Count - scored.Count, keywordOnly,
                (decimal?)Math.Round(correct * 100m / rows.Count, 2, MidpointRounding.AwayFromZero));

            var r = PromptAccuracyCalculator.Calculate(rows);
            var actual = (r.Total, r.Correct, r.Missed, r.WrongPick, r.Unscored, r.KeywordOnly, r.AccuracyPercent);

            Assert.True(expected == actual, $"Vòng {round}: mong đợi {expected}, thực tế {actual}");
            Assert.Equal(r.Total, r.Correct + r.Missed + r.WrongPick + r.Unscored);
        }
    }

    [Fact]
    public void Dau_vao_null_bi_tu_choi_ro_rang() =>
        Assert.Throws<ArgumentNullException>(() => PromptAccuracyCalculator.Calculate(null!));
}
