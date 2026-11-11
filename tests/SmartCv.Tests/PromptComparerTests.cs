using SmartCv.Core.Evaluation;

namespace SmartCv.Tests;

/// <summary>A17: so sánh hai lượt PromptTest (hàm thuần, không database, không Gemini).</summary>
public class PromptComparerTests
{
    private static readonly RunSettings Demo = new(1, "gemini-2.5-flash", 0.20m, 0.30m, 0.70m, 70m);

    // Dữ liệu demo (seed_demo.sql): hồ sơ 1..10, nhãn HR và dự đoán của v1 / v2
    private static readonly bool[] Hr = [true, true, false, true, false, true, false, true, false, true];
    private static readonly bool[] V1 = [true, false, false, true, true, true, false, true, true, true];   // 7/10, bỏ sót 1 (#2), chọn nhầm 2 (#5, #9)
    private static readonly bool[] V2 = [true, false, false, true, true, true, false, true, false, true];  // 8/10, bỏ sót 1 (#2), chọn nhầm 1 (#5)

    private static List<LabeledPrediction> Predictions(bool[] predicted, Func<int, LabeledPrediction, LabeledPrediction>? edit = null) =>
        predicted.Select((p, i) =>
        {
            var prediction = new LabeledPrediction(i + 1, Hr[i], "Completed", p, "Combined");
            return edit is null ? prediction : edit(i + 1, prediction);
        }).ToList();

    private static CompareRun Run(int runId, string name, IReadOnlyCollection<LabeledPrediction> predictions,
        RunSettings? settings = null, string status = "Completed") =>
        new(runId, name, status, settings ?? Demo, predictions);

    private static PromptComparison DemoCompare() =>
        PromptComparer.Compare(Run(1, "v1-co-ban", Predictions(V1)), Run(2, "v2-tieu-chi-ro-rang", Predictions(V2)));

    [Theory]
    [InlineData(PredictionOutcome.Missed, PredictionOutcome.Correct, CompareChange.Fixed)]
    [InlineData(PredictionOutcome.WrongPick, PredictionOutcome.Correct, CompareChange.Fixed)]
    [InlineData(PredictionOutcome.Correct, PredictionOutcome.WrongPick, CompareChange.Broken)]
    [InlineData(PredictionOutcome.Correct, PredictionOutcome.Missed, CompareChange.Broken)]
    [InlineData(PredictionOutcome.Correct, PredictionOutcome.Correct, CompareChange.Unchanged)]
    [InlineData(PredictionOutcome.Missed, PredictionOutcome.WrongPick, CompareChange.Unchanged)]
    [InlineData(PredictionOutcome.Missed, PredictionOutcome.Missed, CompareChange.Unchanged)]
    [InlineData(PredictionOutcome.Unscored, PredictionOutcome.Correct, CompareChange.Unscored)]
    [InlineData(PredictionOutcome.Correct, PredictionOutcome.Unscored, CompareChange.Unscored)]
    [InlineData(PredictionOutcome.Unscored, PredictionOutcome.Unscored, CompareChange.Unscored)]
    public void Phan_loai_thay_doi_tung_CV(PredictionOutcome before, PredictionOutcome after, CompareChange expected) =>
        Assert.Equal(expected, PromptComparer.ClassifyChange(before, after));

    [Fact]
    public void Du_lieu_demo_v1_70_v2_80_la_Better_cong_bang_sua_dung_1_CV()
    {
        var c = DemoCompare();

        Assert.Equal((70m, 7, 1, 2), (c.Base.AccuracyPercent!.Value, c.Base.Correct, c.Base.Missed, c.Base.WrongPick));
        Assert.Equal((80m, 8, 1, 1), (c.Candidate.AccuracyPercent!.Value, c.Candidate.Correct, c.Candidate.Missed, c.Candidate.WrongPick));
        Assert.Equal(CompareVerdicts.Better, c.Verdict);
        Assert.Equal(10m, c.AccuracyDelta);
        Assert.Equal((1, 0, -1), (c.CorrectDelta, c.MissedDelta, c.WrongPickDelta));
        Assert.Equal((1, 0, 9, 0), (c.FixedCount, c.BrokenCount, c.UnchangedCount, c.UnscoredCount));
        Assert.True(c.IsFairComparison);
        Assert.Empty(c.Warnings);
        Assert.Equal(CompareChange.Fixed, c.Items.Single(i => i.ApplicationId == 9).Change);
        Assert.Equal(
            "v2-tieu-chi-ro-rang đúng 80% (8/10 CV), cao hơn v1-co-ban 10 điểm phần trăm (70%, 7/10 CV). " +
            "Sửa đúng thêm 1 CV, không làm sai thêm CV nào. Bỏ sót giữ nguyên (1 → 1), chọn nhầm giảm 1 (2 → 1).",
            c.Summary);
    }

    [Fact]
    public void Doi_bo_goc_va_ban_thu_la_Worse_va_Broken()
    {
        var c = PromptComparer.Compare(Run(2, "v2", Predictions(V2)), Run(1, "v1", Predictions(V1)));

        Assert.Equal(CompareVerdicts.Worse, c.Verdict);
        Assert.Equal(-10m, c.AccuracyDelta);
        Assert.Equal((-1, 0, 1), (c.CorrectDelta, c.MissedDelta, c.WrongPickDelta));
        Assert.Equal((0, 1), (c.FixedCount, c.BrokenCount));
        Assert.Equal(CompareChange.Broken, c.Items.Single(i => i.ApplicationId == 9).Change);
        Assert.Equal("v1 đúng 70% (7/10 CV), thấp hơn v2 10 điểm phần trăm (80%, 8/10 CV). " +
                     "Không sửa đúng thêm CV nào, làm sai thêm 1 CV. Bỏ sót giữ nguyên (1 → 1), chọn nhầm tăng 1 (1 → 2).", c.Summary);
    }

    [Fact]
    public void Cung_ty_le_nhung_sai_o_CV_khac_la_Same_va_neu_ro_sua_dung_lam_sai()
    {
        var candidate = (bool[])V2.Clone();
        candidate[0] = false;   // #1: HR Đạt → bỏ sót (Broken)
        candidate[1] = true;    // #2: HR Đạt → đúng (Fixed)
        var c = PromptComparer.Compare(Run(1, "v2", Predictions(V2)), Run(2, "v3", Predictions(candidate)));

        Assert.Equal(CompareVerdicts.Same, c.Verdict);
        Assert.Equal(0m, c.AccuracyDelta);
        Assert.Equal((1, 1), (c.FixedCount, c.BrokenCount));
        Assert.True(c.IsFairComparison);
        Assert.Equal("v3 đúng 80% (8/10 CV), bằng v2. Sửa đúng thêm 1 CV, làm sai thêm 1 CV. " +
                     "Bỏ sót giữ nguyên (1 → 1), chọn nhầm giữ nguyên (1 → 1).", c.Summary);
    }

    [Fact]
    public void Ghep_theo_ApplicationId_khong_theo_vi_tri_trong_danh_sach()
    {
        var shuffled = Predictions(V2);
        shuffled.Reverse();
        (shuffled[2], shuffled[7]) = (shuffled[7], shuffled[2]);

        var c = PromptComparer.Compare(Run(1, "v1-co-ban", Predictions(V1)), Run(2, "v2-tieu-chi-ro-rang", shuffled));
        var expected = DemoCompare();

        Assert.Equal(expected.Summary, c.Summary);
        Assert.Equal((1, 0, 9), (c.FixedCount, c.BrokenCount, c.UnchangedCount));
        Assert.All(c.Items, i => Assert.Equal(i.ApplicationId, i.Candidate.ApplicationId));
        Assert.Equal(CompareChange.Fixed, c.Items.Single(i => i.ApplicationId == 9).Change);
    }

    public static TheoryData<RunSettings, string> SettingChanges => new()
    {
        { Demo with { ModelName = "gemini-2.5-pro" }, "model" },
        { Demo with { Temperature = 0.7m }, "temperature" },
        { Demo with { KeywordWeight = 0.4m, AiWeight = 0.6m }, "trọng số" },
        { Demo with { AiWeight = 0.71m }, "trọng số" },
        { Demo with { PassThreshold = 75m }, "ngưỡng Đạt" },
        { Demo with { EvaluationSetId = 2 }, "bộ CV" }
    };

    [Theory]
    [MemberData(nameof(SettingChanges))]
    public void Khac_cai_dat_khong_cong_bang_va_NotComparable(RunSettings candidateSettings, string expectedWarning)
    {
        var c = PromptComparer.Compare(Run(1, "v1", Predictions(V1)), Run(2, "v2", Predictions(V2), candidateSettings));

        Assert.False(c.IsFairComparison);
        Assert.Equal(CompareVerdicts.NotComparable, c.Verdict);
        Assert.Single(c.Warnings);
        Assert.Contains(expectedWarning, c.Warnings[0]);
        Assert.StartsWith("Chưa thể so sánh v2 với v1", c.Summary);
        // Số liệu vẫn có để tham khảo
        Assert.Equal(10m, c.AccuracyDelta);
        Assert.Equal(1, c.FixedCount);
    }

    [Fact]
    public void Ten_model_chi_khac_chu_hoa_van_cung_model()
    {
        var c = PromptComparer.Compare(Run(1, "v1", Predictions(V1)),
            Run(2, "v2", Predictions(V2), Demo with { ModelName = "Gemini-2.5-Flash" }));
        Assert.True(c.IsFairComparison);
        Assert.Equal(CompareVerdicts.Better, c.Verdict);
    }

    [Fact]
    public void Cung_cai_dat_cung_bo_nhung_khac_danh_sach_ApplicationId_cung_so_luong_khong_cong_bang()
    {
        // Mỗi bên 10 CV, cùng EvaluationSetId và cài đặt, nhưng #10 chỉ có ở bản gốc và #11 chỉ có ở bản thử
        var candidate = Predictions(V2).Take(9).Append(new LabeledPrediction(11, true, "Completed", true, "Combined")).ToList();
        var c = PromptComparer.Compare(Run(1, "v1", Predictions(V1)), Run(2, "v2", candidate));

        Assert.Equal(10, candidate.Count);
        Assert.False(c.IsFairComparison);
        Assert.Equal(CompareVerdicts.NotComparable, c.Verdict);
        Assert.Equal(9, c.Items.Count);
        Assert.Equal(9, c.Base.Total);
        var warning = Assert.Single(c.Warnings);
        Assert.Contains("không cùng danh sách CV", warning);
        Assert.Contains("1 CV chỉ có trong lượt #1 (v1): hồ sơ #10", warning);
        Assert.Contains("1 CV chỉ có trong lượt #2 (v2): hồ sơ #11", warning);
        Assert.Contains("9 CV chung", warning);
    }

    [Fact]
    public void Ban_thu_thieu_CV_khong_cong_bang_va_canh_bao_ten_CV()
    {
        var c = PromptComparer.Compare(Run(1, "v1", Predictions(V1)), Run(2, "v2", Predictions(V2).Take(8).ToList()),
            id => $"CV-{id}");

        Assert.False(c.IsFairComparison);
        Assert.Contains(c.Warnings, w => w.Contains("2 CV chỉ có trong lượt #1 (v1): CV-9, CV-10"));
        Assert.Equal(8, c.Base.Total);
        Assert.Equal(8, c.Candidate.Total);
    }

    [Fact]
    public void Danh_sach_CV_lech_dai_chi_liet_ke_10_CV_dau()
    {
        var many = Enumerable.Range(100, 15).Select(id => new LabeledPrediction(id, true, "Completed", true, "Combined"));
        var c = PromptComparer.Compare(Run(1, "v1", Predictions(V1).Concat(many).ToList()), Run(2, "v2", Predictions(V2)));
        Assert.Contains(c.Warnings, w => w.Contains("15 CV chỉ có trong lượt #1") && w.Contains("hồ sơ #109 và 5 CV khác"));
    }

    [Theory]
    [InlineData("Running", "Completed")]
    [InlineData("Completed", "Queued")]
    [InlineData("Failed", "Completed")]
    public void Luot_chua_hoan_thanh_khong_so_sanh_duoc(string baseStatus, string candidateStatus)
    {
        var c = PromptComparer.Compare(Run(1, "v1", Predictions(V1), status: baseStatus),
            Run(2, "v2", Predictions(V2), status: candidateStatus));

        Assert.False(c.IsFairComparison);
        Assert.Equal(CompareVerdicts.NotComparable, c.Verdict);
        Assert.Contains(c.Warnings, w => w.Contains("chưa hoàn thành"));
    }

    [Fact]
    public void Nhan_HR_khac_nhau_giua_hai_bo_bi_loai_va_canh_bao()
    {
        var candidate = Predictions(V2, (id, p) => id == 3 ? p with { HrIsPass = true } : p);
        var c = PromptComparer.Compare(Run(1, "v1", Predictions(V1)),
            Run(2, "v2", candidate, Demo with { EvaluationSetId = 2 }));

        Assert.False(c.IsFairComparison);
        Assert.DoesNotContain(c.Items, i => i.ApplicationId == 3);
        Assert.Contains(c.Warnings, w => w.Contains("nhãn HR khác nhau") && w.Contains("hồ sơ #3"));
    }

    [Fact]
    public void Khong_co_CV_chung_thi_NotComparable_va_khong_co_ty_le()
    {
        var other = Enumerable.Range(50, 3).Select(id => new LabeledPrediction(id, true, "Completed", true, "Combined")).ToList();
        var c = PromptComparer.Compare(Run(1, "v1", Predictions(V1)), Run(2, "v2", other));

        Assert.Equal(CompareVerdicts.NotComparable, c.Verdict);
        Assert.False(c.IsFairComparison);
        Assert.Null(c.AccuracyDelta);
        Assert.Null(c.Base.AccuracyPercent);
        Assert.Empty(c.Items);
        Assert.Contains(c.Warnings, w => w.Contains("không có CV chung"));
    }

    [Fact]
    public void Hai_luot_rong_khong_loi_va_NotComparable()
    {
        var c = PromptComparer.Compare(Run(1, "v1", []), Run(2, "v2", []));
        Assert.Equal(CompareVerdicts.NotComparable, c.Verdict);
        Assert.Null(c.AccuracyDelta);
        Assert.Equal((0, 0, 0, 0), (c.FixedCount, c.BrokenCount, c.UnchangedCount, c.UnscoredCount));
        Assert.False(string.IsNullOrWhiteSpace(c.Summary));
    }

    [Fact]
    public void CV_KeywordOnly_khong_lam_doi_ket_luan_van_canh_bao_ro()
    {
        // #1 chỉ chấm từ khóa ở bản thử nhưng vẫn đúng như bản gốc; cải thiện (#9) do Gemini chấm ở cả hai lượt
        var candidate = Predictions(V2, (id, p) => id == 1 ? p with { ScoreSource = "KeywordOnly" } : p);
        var c = PromptComparer.Compare(Run(1, "v1", Predictions(V1)), Run(2, "v2", candidate));

        Assert.Equal(CompareVerdicts.Better, c.Verdict);
        Assert.True(c.IsFairComparison);
        Assert.Equal(1, c.KeywordOnlyAffectedCount);
        Assert.Contains(c.Warnings, w => w.Contains("1 CV chỉ được chấm bằng từ khóa") && w.Contains("không phản ánh tác động của system prompt"));
        Assert.EndsWith("Lưu ý: có 1 CV chỉ chấm bằng từ khóa, xem phần cảnh báo.", c.Summary);
    }

    [Fact]
    public void Cai_thien_chi_den_tu_CV_KeywordOnly_thi_khong_tuyen_bo_prompt_tot_hon()
    {
        // CV duy nhất được sửa đúng (#9) lại là kết quả chỉ chấm từ khóa ở bản thử
        var candidate = Predictions(V2, (id, p) => id == 9 ? p with { ScoreSource = "KeywordOnly" } : p);
        var c = PromptComparer.Compare(Run(1, "v1", Predictions(V1)), Run(2, "v2", candidate));

        Assert.True(c.IsFairComparison);   // điều kiện chạy vẫn giống nhau
        Assert.Equal(CompareVerdicts.NotComparable, c.Verdict);
        Assert.Equal(10m, c.AccuracyDelta);   // số liệu vẫn trả về để tham khảo
        Assert.Contains(c.Warnings, w => w.Contains("Chưa đủ cơ sở kết luận"));
        Assert.StartsWith("Chưa thể kết luận v2 tốt hơn hay kém hơn v1", c.Summary);
    }

    [Fact]
    public void Phan_lon_CV_chi_cham_tu_khoa_thi_NotComparable()
    {
        var candidate = Predictions(V2, (id, p) => id <= 6 ? p with { ScoreSource = "KeywordOnly" } : p);
        var c = PromptComparer.Compare(Run(1, "v1", Predictions(V1)), Run(2, "v2", candidate));

        Assert.Equal(CompareVerdicts.NotComparable, c.Verdict);
        Assert.Equal(6, c.KeywordOnlyAffectedCount);
        Assert.Contains(c.Warnings, w => w.Contains("chỉ 4/10 CV được Gemini chấm ở cả hai lượt"));
    }

    [Fact]
    public void CV_khong_cham_duoc_van_o_mau_so_va_khong_tinh_Fixed_Broken()
    {
        // #1 lỗi ở cả hai lượt: vẫn trong mẫu số, Change = Unscored, kết luận vẫn dựa trên #9
        var edit = (int id, LabeledPrediction p) => id == 1 ? p with { Status = "Failed", PredictedPass = null } : p;
        var c = PromptComparer.Compare(Run(1, "v1", Predictions(V1, edit)), Run(2, "v2", Predictions(V2, edit)));

        Assert.Equal((10, 6, 1), (c.Base.Total, c.Base.Correct, c.Base.Unscored));
        Assert.Equal((60m, 70m), (c.Base.AccuracyPercent!.Value, c.Candidate.AccuracyPercent!.Value));
        Assert.Equal(CompareChange.Unscored, c.Items.Single(i => i.ApplicationId == 1).Change);
        Assert.Equal((1, 0, 8, 1), (c.FixedCount, c.BrokenCount, c.UnchangedCount, c.UnscoredCount));
        Assert.Equal(CompareVerdicts.Better, c.Verdict);
        Assert.Contains(c.Warnings, w => w.Contains("1 CV không chấm được") && w.Contains("mẫu số"));
        Assert.EndsWith("Lưu ý: có 1 CV không chấm được, xem phần cảnh báo.", c.Summary);
    }

    [Fact]
    public void Chenh_lech_chi_do_CV_loi_o_ban_goc_thi_khong_ket_luan()
    {
        // Bản gốc lỗi #1 (bản thử đúng): tỷ lệ cao hơn nhưng không phải nhờ prompt
        var c = PromptComparer.Compare(
            Run(1, "v2", Predictions(V2, (id, p) => id == 1 ? p with { Status = "Pending", PredictedPass = null } : p)),
            Run(2, "v3", Predictions(V2)));

        Assert.Equal(10m, c.AccuracyDelta);
        Assert.Equal((0, 0, 1), (c.FixedCount, c.BrokenCount, c.UnscoredCount));
        Assert.Equal(CompareVerdicts.NotComparable, c.Verdict);
    }

    [Fact]
    public void Du_lieu_null_duoc_xu_ly_ro_rang()
    {
        Assert.Throws<ArgumentNullException>(() => PromptComparer.Compare(null!, Run(2, "v2", [])));
        Assert.Throws<ArgumentNullException>(() => PromptComparer.Compare(Run(1, "v1", []), null!));
        Assert.Throws<ArgumentNullException>(() => PromptComparer.Compare(Run(1, "v1", null!), Run(2, "v2", [])));

        // ScoreSource null (dữ liệu cũ) không bị coi là KeywordOnly; Completed nhưng PredictedPass null = không chấm được
        var candidate = Predictions(V2, (id, p) => id switch
        {
            1 => p with { ScoreSource = null },
            2 => p with { PredictedPass = null },
            _ => p
        });
        var c = PromptComparer.Compare(Run(1, "v1", Predictions(V1)), Run(2, "v2", candidate));
        Assert.Equal(0, c.KeywordOnlyAffectedCount);
        Assert.Equal(CompareChange.Unscored, c.Items.Single(i => i.ApplicationId == 2).Change);
    }

    [Fact]
    public void Trung_ApplicationId_trong_mot_luot_bi_tu_choi()
    {
        var duplicated = Predictions(V1).Append(new LabeledPrediction(1, true, "Completed", true, "Combined")).ToList();
        Assert.Throws<ArgumentException>(() => PromptComparer.Compare(Run(1, "v1", duplicated), Run(2, "v2", Predictions(V2))));
    }

    [Fact]
    public void Tom_tat_dung_dau_phay_thap_phan_tieng_Viet()
    {
        // 1/3 = 33,33% → 2/3 = 66,67%: chênh 33,34 điểm phần trăm
        bool[] labels = [true, false, true];
        var a = labels.Select((h, i) => new LabeledPrediction(i + 1, h, "Completed", i == 0 ? h : !h, "Combined")).ToList();
        var b = labels.Select((h, i) => new LabeledPrediction(i + 1, h, "Completed", i < 2 ? h : !h, "Combined")).ToList();
        var c = PromptComparer.Compare(Run(1, "A", a), Run(2, "B", b));

        Assert.Equal(33.34m, c.AccuracyDelta);
        Assert.StartsWith("B đúng 66,67% (2/3 CV), cao hơn A 33,34 điểm phần trăm (33,33%, 1/3 CV).", c.Summary);
    }

    [Theory]
    [InlineData(70, 80, true, "Better")]
    [InlineData(80, 70, true, "Worse")]
    [InlineData(80, 80, true, "Same")]
    [InlineData(70, 80, false, "NotComparable")]
    public void Ket_luan_theo_ty_le(double a, double b, bool fair, string expected) =>
        Assert.Equal(expected, PromptComparer.Verdict((decimal)a, (decimal)b, fair));

    [Fact]
    public void Chua_co_ty_le_thi_khong_so_sanh_duoc() =>
        Assert.Equal(CompareVerdicts.NotComparable, PromptComparer.Verdict(null, 80m, isFair: true));
}
