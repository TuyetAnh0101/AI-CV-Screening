using SmartCv.Core.Constants;
using SmartCv.Core.Evaluation;

// Thư mục Evaluation/ nhưng giữ namespace gốc (xem PromptAccuracyRuleTests).
namespace SmartCv.Tests;

/// <summary>
/// A18.4: các tính chất nghiệp vụ của PromptComparer (A17) kiểm trên nhiều bộ dữ liệu sinh ngẫu nhiên với hạt giống cố định.
/// Từng nhánh Fixed / Broken / Unchanged / Unscored, Better / Worse / Same / NotComparable, điều kiện so sánh công bằng
/// (bộ CV, model, temperature, trọng số, ngưỡng) và câu tóm tắt của dữ liệu demo đã có trong PromptComparerTests (A17).
/// </summary>
public class PromptComparerRuleTests
{
    private static readonly RunSettings Conditions = new(1, "gemini-2.5-flash", 0.20m, 0.30m, 0.70m, 70m);

    private sealed record Scenario(int Round, List<LabeledPrediction> Base, List<LabeledPrediction> Candidate);

    /// <summary>Hai lượt trên cùng danh sách hồ sơ và cùng nhãn HR; mỗi bên có thể có CV lỗi hoặc chỉ chấm từ khóa.</summary>
    private static IEnumerable<Scenario> Scenarios(int seed, int count)
    {
        var random = new Random(seed);
        for (var round = 0; round < count; round++)
        {
            var ids = Enumerable.Range(1, 60).OrderBy(_ => random.Next()).Take(random.Next(1, 26)).ToList();
            var labels = ids.ToDictionary(id => id, _ => random.Next(2) == 0);
            List<LabeledPrediction> Side() => ids.Select(id => random.Next(10) switch
            {
                0 => new LabeledPrediction(id, labels[id], EvaluationStatuses.Failed, null),
                1 => new LabeledPrediction(id, labels[id], EvaluationStatuses.Completed, random.Next(2) == 0, ScoreSources.KeywordOnly),
                _ => new LabeledPrediction(id, labels[id], EvaluationStatuses.Completed, random.Next(2) == 0, ScoreSources.Combined)
            }).ToList();
            yield return new(round, Side(), Side());
        }
    }

    private static CompareRun Run(int runId, IReadOnlyCollection<LabeledPrediction> predictions) =>
        new(runId, $"v{runId}", RunStatuses.Completed, Conditions, predictions);

    private static bool IsCorrect(PredictionOutcome outcome) => outcome == PredictionOutcome.Correct;

    [Fact]
    public void So_lieu_so_sanh_luon_dung_cong_thuc_ty_le_dung_A16_va_phan_loai_tung_CV()
    {
        foreach (var s in Scenarios(seed: 1711, count: 300))
        {
            var c = PromptComparer.Compare(Run(1, s.Base), Run(2, s.Candidate));
            var at = $"Vòng {s.Round}";

            // Mỗi bên dùng đúng công thức A16 trên cùng mẫu số
            Assert.True(PromptAccuracyCalculator.Calculate(s.Base) == c.Base, $"{at}: số liệu bản gốc khác A16");
            Assert.True(PromptAccuracyCalculator.Calculate(s.Candidate) == c.Candidate, $"{at}: số liệu bản thử khác A16");
            Assert.True(c.AccuracyDelta == c.Candidate.AccuracyPercent - c.Base.AccuracyPercent, $"{at}: AccuracyDelta sai");
            Assert.True((c.CorrectDelta, c.MissedDelta, c.WrongPickDelta) ==
                        (c.Candidate.Correct - c.Base.Correct, c.Candidate.Missed - c.Base.Missed, c.Candidate.WrongPick - c.Base.WrongPick),
                $"{at}: chênh lệch đúng / bỏ sót / chọn nhầm sai");

            // Mỗi CV thuộc đúng một nhóm; CV không chấm được không bao giờ là Fixed / Broken
            Assert.True(c.FixedCount + c.BrokenCount + c.UnchangedCount + c.UnscoredCount == c.Items.Count, $"{at}: tổng nhóm ≠ số CV");
            Assert.True(c.Items.Count == s.Base.Count, $"{at}: thiếu CV khi so sánh");
            foreach (var i in c.Items)
            {
                var expected = i.BaseOutcome == PredictionOutcome.Unscored || i.CandidateOutcome == PredictionOutcome.Unscored
                    ? CompareChange.Unscored
                    : (IsCorrect(i.BaseOutcome), IsCorrect(i.CandidateOutcome)) switch
                    {
                        (false, true) => CompareChange.Fixed,
                        (true, false) => CompareChange.Broken,
                        _ => CompareChange.Unchanged
                    };
                Assert.True(expected == i.Change, $"{at}: hồ sơ #{i.ApplicationId} phải là {expected}, thực tế {i.Change}");
            }

            // Chênh lệch số CV đúng = sửa đúng − làm sai + phần do CV không chấm được ở một bên
            var unscoredGain = c.Items.Count(i => i.BaseOutcome == PredictionOutcome.Unscored && IsCorrect(i.CandidateOutcome));
            var unscoredLoss = c.Items.Count(i => IsCorrect(i.BaseOutcome) && i.CandidateOutcome == PredictionOutcome.Unscored);
            Assert.True(c.CorrectDelta == c.FixedCount - c.BrokenCount + unscoredGain - unscoredLoss, $"{at}: CorrectDelta không khớp Fixed/Broken");

            // Kết luận không bao giờ ngược chiều với tỷ lệ đúng
            Assert.True(c.IsFairComparison, $"{at}: cùng điều kiện phải là so sánh công bằng");
            Assert.True(c.Verdict switch
            {
                CompareVerdicts.Better => c.AccuracyDelta > 0,
                CompareVerdicts.Worse => c.AccuracyDelta < 0,
                CompareVerdicts.Same => c.AccuracyDelta == 0,
                CompareVerdicts.NotComparable => true,
                _ => false
            }, $"{at}: kết luận {c.Verdict} ngược chiều AccuracyDelta {c.AccuracyDelta}");
            Assert.False(string.IsNullOrWhiteSpace(c.Summary), $"{at}: thiếu câu tóm tắt");
        }
    }

    [Fact]
    public void Doi_vai_tro_ban_goc_va_ban_thu_thi_ket_qua_doi_xung()
    {
        foreach (var s in Scenarios(seed: 2026, count: 300))
        {
            var forward = PromptComparer.Compare(Run(1, s.Base), Run(2, s.Candidate));
            var backward = PromptComparer.Compare(Run(2, s.Candidate), Run(1, s.Base));
            var at = $"Vòng {s.Round}";

            Assert.True(forward.AccuracyDelta == -backward.AccuracyDelta, $"{at}: AccuracyDelta không đổi dấu");
            Assert.True((forward.CorrectDelta, forward.MissedDelta, forward.WrongPickDelta) ==
                        (-backward.CorrectDelta, -backward.MissedDelta, -backward.WrongPickDelta), $"{at}: chênh lệch không đổi dấu");
            Assert.True((forward.FixedCount, forward.BrokenCount, forward.UnchangedCount, forward.UnscoredCount) ==
                        (backward.BrokenCount, backward.FixedCount, backward.UnchangedCount, backward.UnscoredCount),
                $"{at}: Fixed và Broken không hoán đổi");
            var mirrored = forward.Verdict switch
            {
                CompareVerdicts.Better => CompareVerdicts.Worse,
                CompareVerdicts.Worse => CompareVerdicts.Better,
                _ => forward.Verdict
            };
            Assert.True(mirrored == backward.Verdict, $"{at}: {forward.Verdict} khi đảo chiều phải là {mirrored}, thực tế {backward.Verdict}");
        }
    }

    [Fact]
    public void Thu_tu_CV_trong_danh_sach_khong_anh_huong_ket_qua()
    {
        var random = new Random(37);
        foreach (var s in Scenarios(seed: 37, count: 200))
        {
            var original = PromptComparer.Compare(Run(1, s.Base), Run(2, s.Candidate));
            var shuffled = PromptComparer.Compare(
                Run(1, s.Base.OrderBy(_ => random.Next()).ToList()), Run(2, s.Candidate.OrderBy(_ => random.Next()).ToList()));
            var at = $"Vòng {s.Round}";

            Assert.True((original.Verdict, original.Summary, original.AccuracyDelta, original.FixedCount, original.BrokenCount) ==
                        (shuffled.Verdict, shuffled.Summary, shuffled.AccuracyDelta, shuffled.FixedCount, shuffled.BrokenCount),
                $"{at}: đổi thứ tự CV làm đổi kết quả");
            Assert.Equal(original.Items.Select(i => (i.ApplicationId, i.Change)), shuffled.Items.Select(i => (i.ApplicationId, i.Change)));
            Assert.Equal(original.Warnings, shuffled.Warnings);
        }
    }
}
