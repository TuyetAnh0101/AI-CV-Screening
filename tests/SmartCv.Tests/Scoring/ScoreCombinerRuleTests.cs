using SmartCv.Core.Ai;
using SmartCv.Core.Constants;
using SmartCv.Core.Scoring;

// Thư mục Scoring/ nhưng giữ namespace gốc: namespace con trùng tên kiểu đang dùng trong các test khác
// (ví dụ SmartCv.Tests.Evaluation che entity Evaluation) gây lỗi CS0118.
namespace SmartCv.Tests;

/// <summary>
/// A18.1: quy tắc gộp điểm keyword + AI (ScoreCombiner, A10): 30% keyword + 70% AI, làm tròn 2 chữ số,
/// ngưỡng Đạt 70, AI lỗi → KeywordOnly, thiếu kỹ năng bắt buộc → chặn theo cấu hình (mặc định 69).
/// Bổ sung cho ScoreCombinerTests (A10); không lặp lại các trường hợp đã có ở đó.
/// </summary>
public class ScoreCombinerRuleTests
{
    private const decimal KeywordWeight = 0.3m, AiWeight = 0.7m, PassThreshold = 70m, Cap = 69m;

    // Điểm keyword và điểm AI của 10 hồ sơ demo (seed_demo.sql, run 1 = v1, run 2 = v2). Điểm cuối: PromptCompareSeed.
    private static readonly decimal[] DemoKeyword = [100m, 80m, 10m, 100m, 66.67m, 100m, 23.33m, 100m, 46.67m, 100m];
    private static readonly decimal[] DemoAiV1 = [88m, 58m, 35m, 90m, 75m, 92m, 72m, 82m, 88m, 89m];
    private static readonly decimal[] DemoAiV2 = [90m, 60m, 30m, 92m, 75m, 94m, 45m, 85m, 66m, 91m];

    private static SkillMatch Must(string code, bool matched) =>
        new(code, code.ToUpperInvariant(), true, matched, matched ? $"CV có {code}" : null);

    private static SkillMatch Nice(string code, bool matched) =>
        new(code, code.ToUpperInvariant(), false, matched, matched ? $"CV có {code}" : null);

    /// <summary>Mặc định đủ kỹ năng bắt buộc để quy tắc chặn không can thiệp.</summary>
    private static KeywordScoreResult Keyword(decimal score, params SkillMatch[] skills) =>
        new(score, skills.Length > 0 ? skills : [Must("csharp", true)], Gpa: null, YearsExperience: 1.5m, Notes: []);

    private static AiEvaluationResult Ai(decimal score, params string[] matched) =>
        new() { IsSuccess = true, Score = score, MatchedSkillCodes = matched, Reasoning = "Nhận xét của Gemini.", Attempts = 1 };

    private static CombinedScore Combine(KeywordScoreResult keyword, AiEvaluationResult? ai, decimal? cap = Cap,
        decimal threshold = PassThreshold) =>
        ScoreCombiner.Combine(keyword, ai, KeywordWeight, AiWeight, threshold, cap);

    public static TheoryData<int, int> DemoRows()
    {
        var rows = new TheoryData<int, int>();
        foreach (var version in new[] { 1, 2 })
            for (var index = 0; index < DemoKeyword.Length; index++) rows.Add(version, index);
        return rows;
    }

    [Theory]
    [MemberData(nameof(DemoRows))]
    public void Cong_thuc_30_70_tai_tao_dung_diem_cuoi_va_ket_qua_Dat_cua_du_lieu_demo(int version, int index)
    {
        // seed_demo.sql được sinh trước khi có quy tắc chặn khi thiếu kỹ năng bắt buộc, nên so với công thức gốc (cap = null).
        // Kết quả Đạt (≥ 70) chính là vector dự đoán v1/v2 mà A16/A17 dùng (70% và 80%).
        var ai = version == 1 ? DemoAiV1[index] : DemoAiV2[index];
        var expected = version == 1 ? PromptCompareSeed.V1Scores[index] : PromptCompareSeed.V2Scores[index];

        var r = Combine(Keyword(DemoKeyword[index]), Ai(ai), cap: null);

        Assert.Equal(expected, r.FinalScore);
        Assert.Equal(expected >= PassThreshold, r.PredictedPass);
        Assert.Equal(FitLevels.FromScore(expected), r.FitLabel);
        Assert.Equal((ScoreSources.Combined, (decimal?)ai), (r.ScoreSource, r.AiScore));
    }

    [Theory]
    [InlineData(70, 70, 70.00, true)]
    [InlineData(0, 100, 70.00, true)]
    [InlineData(100, 57.14, 70.00, true)]    // 30 + 39,998 = 69,998 → 70,00: kết quả Đạt khớp điểm hiển thị
    [InlineData(100, 57.13, 69.99, false)]   // 30 + 39,991 = 69,991 → 69,99
    public void Nguong_70_xet_tren_diem_cuoi_da_lam_tron(double keyword, double ai, double expected, bool pass)
    {
        var r = Combine(Keyword((decimal)keyword), Ai((decimal)ai));

        Assert.Equal((decimal)expected, r.FinalScore);
        Assert.Equal(pass, r.PredictedPass);
        Assert.Equal(pass ? FitLevels.Good : FitLevels.Potential, r.FitLabel);
        Assert.Equal(ScoreSources.Combined, r.ScoreSource);
    }

    [Fact]
    public void Khong_co_ket_qua_AI_thi_chi_dung_keyword_va_ghi_ro_ly_do()
    {
        var r = Combine(Keyword(85), ai: null);

        Assert.Equal((85m, (decimal?)null, ScoreSources.KeywordOnly), (r.FinalScore, r.AiScore, r.ScoreSource));
        Assert.Equal("Không gọi được AI", r.ErrorMessage);
        Assert.Equal("Không gọi được AI; điểm chỉ tính theo từ khóa.", r.Reasoning);
        Assert.Equal((FitLevels.Excellent, true), (r.FitLabel, r.PredictedPass));
        Assert.Equal(1.5m, r.YearsExperience);   // không có AI thì lấy số năm keyword đọc được
        Assert.False(r.IsSimulated);
    }

    [Fact]
    public void AI_bao_thanh_cong_nhung_khong_co_diem_van_la_KeywordOnly_va_khong_xac_nhan_ky_nang()
    {
        var noScore = new AiEvaluationResult { IsSuccess = true, Score = null, MatchedSkillCodes = ["docker"] };

        var r = Combine(Keyword(80, Must("csharp", true), Must("docker", false)), noScore);

        Assert.Equal(ScoreSources.KeywordOnly, r.ScoreSource);
        Assert.Null(r.AiScore);
        Assert.False(r.Skills.Single(s => s.SkillCode == "docker").IsMatched);
        Assert.Equal(Cap, r.FinalScore);   // vẫn thiếu kỹ năng bắt buộc nên bị chặn
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(69, 69)]
    [InlineData(75, 75)]
    [InlineData(95, 93.5)]   // mức chặn cao hơn điểm: điểm giữ nguyên
    public void Muc_chan_khi_thieu_ky_nang_bat_buoc_lay_tu_cau_hinh(double cap, double expected)
    {
        var r = Combine(Keyword(90, Must("csharp", true), Must("docker", false)), Ai(95, "csharp"), cap: (decimal)cap);

        Assert.Equal((decimal)expected, r.FinalScore);
        Assert.Equal(r.FinalScore >= PassThreshold, r.PredictedPass);
        if ((decimal)cap < 93.5m)
            Assert.Contains($"giới hạn {cap:0} vì thiếu kỹ năng bắt buộc: DOCKER.", r.Reasoning);
        else
            Assert.DoesNotContain("giới hạn", r.Reasoning);
    }

    [Fact]
    public void Diem_bang_dung_muc_chan_thi_khong_ghi_ly_do_chan()
    {
        var r = Combine(Keyword(69, Must("docker", false)), Ai(69));

        Assert.Equal(69m, r.FinalScore);
        Assert.DoesNotContain("giới hạn", r.Reasoning);
    }

    [Fact]
    public void Ly_do_chan_chi_liet_ke_ky_nang_bat_buoc_con_thieu_khong_ke_ky_nang_uu_tien()
    {
        var keyword = Keyword(90, Must("csharp", true), Must("sql", false), Must("docker", false), Nice("figma", false));

        var r = Combine(keyword, Ai(95, "csharp"));

        Assert.Equal(Cap, r.FinalScore);
        Assert.Contains("thiếu kỹ năng bắt buộc: SQL, DOCKER.", r.Reasoning);
        Assert.DoesNotContain("FIGMA", r.Reasoning);
    }

    [Fact]
    public void AI_xac_nhan_ky_nang_keyword_bo_sot_khong_phan_biet_hoa_thuong()
    {
        var r = Combine(Keyword(90, Must("csharp", true), Must("docker", false)), Ai(95, "CSHARP", "Docker"));

        Assert.Equal(93.50m, r.FinalScore);
        var docker = r.Skills.Single(s => s.SkillCode == "docker");
        Assert.Equal((true, SkillMatchSources.Ai, ScoreCombiner.AiConfirmedEvidence), (docker.IsMatched, docker.MatchedBy, docker.Evidence));
        var csharp = r.Skills.Single(s => s.SkillCode == "csharp");
        Assert.Equal((SkillMatchSources.Keyword, "CV có csharp"), (csharp.MatchedBy, csharp.Evidence));   // bằng chứng từ CV được giữ
    }

    [Theory]
    [InlineData(100, 150, 100)]
    [InlineData(0, -20, 0)]
    public void Diem_cuoi_luon_nam_trong_0_den_100_ke_ca_khi_AI_tra_diem_ngoai_khoang(double keyword, double ai, double expected)
    {
        var r = Combine(Keyword((decimal)keyword), Ai((decimal)ai));
        Assert.Equal((decimal)expected, r.FinalScore);
        Assert.Equal(FitLevels.FromScore(r.FinalScore), r.FitLabel);
    }

    [Fact]
    public void Diem_cuoi_nguon_diem_muc_danh_gia_va_ket_qua_Dat_luon_nhat_quan()
    {
        decimal[] keywordScores = [0m, 33.33m, 54.99m, 55m, 66.67m, 69m, 69.99m, 70m, 84.99m, 85m, 100m];
        decimal?[] aiScores = [null, 0m, 40m, 57.14m, 69.99m, 70m, 85m, 100m];
        decimal?[] caps = [null, 60m, 69m];
        decimal[] thresholds = [60m, 70m, 80m];
        var checkedCases = 0;

        foreach (var kw in keywordScores)
        foreach (var ai in aiScores)
        foreach (var missingMust in new[] { false, true })
        foreach (var cap in caps)
        foreach (var threshold in thresholds)
        {
            var keyword = Keyword(kw, Must("csharp", true), Must("docker", !missingMust));
            var aiResult = ai is { } score ? Ai(score) : AiEvaluationResult.Failure(AiErrorKind.Network, "Mất kết nối Gemini", 3);
            var r = ScoreCombiner.Combine(keyword, aiResult, KeywordWeight, AiWeight, threshold, cap);
            var context = $"keyword={kw}, ai={ai?.ToString() ?? "lỗi"}, thiếu bắt buộc={missingMust}, chặn={cap?.ToString() ?? "tắt"}, ngưỡng={threshold} → điểm {r.FinalScore}";

            Assert.True(r.FinalScore is >= 0m and <= 100m, $"Điểm ngoài 0–100: {context}");
            Assert.True(decimal.Round(r.FinalScore, 2) == r.FinalScore, $"Điểm có quá 2 chữ số thập phân: {context}");
            Assert.True(r.FitLabel == FitLevels.FromScore(r.FinalScore), $"Mức {r.FitLabel} không khớp điểm: {context}");
            Assert.True(r.PredictedPass == r.FinalScore >= threshold, $"Kết quả Đạt = {r.PredictedPass} không khớp ngưỡng: {context}");
            Assert.True(r.ScoreSource is ScoreSources.Combined or ScoreSources.KeywordOnly, $"Nguồn điểm lạ {r.ScoreSource}: {context}");
            Assert.True((r.ScoreSource == ScoreSources.Combined) == (r.AiScore is not null), $"Nguồn điểm và AiScore lệch nhau: {context}");
            Assert.True((r.ScoreSource == ScoreSources.Combined) == (r.ErrorMessage is null), $"Nguồn điểm và lý do lỗi lệch nhau: {context}");
            if (missingMust && cap is { } limit)
                Assert.True(r.FinalScore <= limit, $"Thiếu kỹ năng bắt buộc nhưng vượt mức chặn: {context}");
            if (r.ScoreSource == ScoreSources.KeywordOnly)
            {
                var expected = missingMust && cap is { } c ? Math.Min(kw, c) : kw;
                Assert.True(r.FinalScore == expected, $"KeywordOnly phải lấy đúng điểm keyword (sau khi chặn): {context}");
            }
            checkedCases++;
        }

        Assert.Equal(keywordScores.Length * aiScores.Length * 2 * caps.Length * thresholds.Length, checkedCases);
    }
}
