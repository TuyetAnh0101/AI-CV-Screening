using System.Globalization;
using SmartCv.Core.Constants;
using SmartCv.Core.Options;
using SmartCv.Core.Scoring;

// Thư mục Scoring/ nhưng giữ namespace gốc (xem ScoreCombinerRuleTests).
namespace SmartCv.Tests;

/// <summary>
/// A18.2: quy tắc chấm keyword (KeywordScoringService, CvFactExtractor, A9). Trọng số mặc định 60 / 20 / 10 / 10;
/// tiêu chí không áp dụng được thì bỏ ra và chia lại trọng số. "Hôm nay" luôn được truyền vào, không đọc đồng hồ máy.
/// Bổ sung cho KeywordScoringTests (A9), nơi đã có: Java ≠ JavaScript, SQL ≠ MySQL/NoSQL, bỏ dấu và từ đồng nghĩa,
/// bằng chứng giữ chữ gốc, GPA thang 4/10, khoảng "– nay", học tập không tính là kinh nghiệm.
/// </summary>
public class KeywordScoringRuleTests
{
    private static readonly ScoringOptions Options = new();
    private static readonly DateOnly Today = new(2026, 11, 10);

    private static SkillProfile Must(string code, string name, params string[] aliases) => new(code, name, SkillTypes.MustHave, aliases);
    private static SkillProfile Nice(string code, string name, params string[] aliases) => new(code, name, SkillTypes.NiceToHave, aliases);
    private static JobProfile Job(decimal? minGpa, decimal minYears, params SkillProfile[] skills) =>
        new(1, "Backend Developer (.NET)", null, minGpa, minYears, skills);

    private static KeywordScoreResult Score(JobProfile job, string text) => KeywordScoringService.Score(job, text, Options, Today);

    [Fact]
    public void Thieu_GPA_va_mot_ky_nang_bat_buoc_thi_chia_lai_trong_so_con_44_44()
    {
        // Bắt buộc 1/2 (0,6 × 0,5) + ưu tiên 0/1 + kinh nghiệm đủ (0,1 × 1) = 0,4; bỏ GPA (10%) nên chia cho 0,9 → 44,44
        var job = Job(3.0m, 1, Must("csharp", "C#"), Must("sql_server", "SQL Server"), Nice("docker", "Docker"));

        var r = Score(job, "1 năm làm việc với C#.");

        Assert.Equal(44.44m, r.Score);
        Assert.Null(r.Gpa);
        Assert.Equal(1m, r.YearsExperience);
        Assert.Contains(r.Notes, n => n.StartsWith("CV không ghi GPA", StringComparison.Ordinal));
        Assert.Equal(["sql_server"], r.MissingMustHaveCodes);
    }

    [Theory]
    [InlineData("Thành thạo C#.", null, 100.0)]                        // không ghi: bỏ tiêu chí, KHÔNG bị chấm như 0 năm
    [InlineData("Thành thạo C#. 6 tháng kinh nghiệm.", 0.5, 89.29)]    // có ghi, thấp hơn yêu cầu 2 năm: (0,6 + 0,1 × 0,25) / 0,7
    [InlineData("Thành thạo C#. 2 năm kinh nghiệm.", 2.0, 100.0)]
    public void Khong_ghi_kinh_nghiem_la_null_va_bo_tieu_chi_con_ghi_it_kinh_nghiem_thi_tinh_theo_ty_le(
        string text, double? expectedYears, double expectedScore)
    {
        var r = Score(Job(null, 2m, Must("csharp", "C#")), text);
        decimal? years = expectedYears is { } value ? (decimal)value : null;

        Assert.Equal(years, r.YearsExperience);
        Assert.Equal((decimal)expectedScore, r.Score);
        Assert.Equal(expectedYears is null, r.Notes.Any(n => n.Contains("không ghi rõ thời gian kinh nghiệm")));
    }

    [Theory]
    [InlineData("GPA 3.0/4. Python. 2 năm kinh nghiệm.")]   // đúng bằng yêu cầu
    [InlineData("GPA 3.9/4. Python. 8 năm kinh nghiệm.")]   // vượt yêu cầu: tối đa đủ điểm, không cộng thêm
    public void GPA_va_kinh_nghiem_dat_hoac_vuot_yeu_cau_thi_du_diem(string text)
    {
        var r = Score(Job(3.0m, 2m, Must("python", "Python")), text);
        Assert.Equal(100m, r.Score);
    }

    [Theory]
    [InlineData("GPA 4.0/4", "4.0")]
    [InlineData("GPA 10/10", "4")]
    [InlineData("Điểm trung bình: 8.0/10", "3.2")]
    [InlineData("GPA: 3,50", "3.50")]
    public void GPA_o_bien_thang_4_va_thang_10_quy_ve_thang_4(string text, string expected)
    {
        var gpa = CvFactExtractor.ExtractGpa(TextNormalizer.Normalize(text));
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), gpa);
    }

    [Theory]
    [InlineData("2026-11-10", 1.5)]   // 06/2025 → 11/2026: 18 tháng
    [InlineData("2027-05-10", 2.0)]   // cùng CV, "hôm nay" khác: 24 tháng
    [InlineData("2025-06-30", 0.1)]   // bắt đầu ngay trong tháng hiện tại: tính 1 tháng
    public void Khoang_den_nay_tinh_theo_ngay_truyen_vao_khong_theo_dong_ho_may(string today, double expected)
    {
        var years = CvFactExtractor.ExtractYearsOfExperience(
            TextNormalizer.Normalize("Backend Developer tại ABC (06/2025 - nay)"), DateOnly.Parse(today, CultureInfo.InvariantCulture));
        Assert.Equal((decimal)expected, years);
    }

    [Theory]
    [InlineData(" \t\r\n ")]
    [InlineData("")]
    public void CV_trong_bi_tu_choi_co_kiem_soat_khong_cham_thanh_0_diem(string text)
    {
        // Hợp đồng A9: CV rỗng không được chấm thành 0 điểm; A11 kiểm tra trước và ghi Failed với lý do riêng.
        var ex = Assert.Throws<ArgumentException>(() =>
            KeywordScoringService.Score(Job(null, 0, Must("csharp", "C#")), text, Options, Today));
        Assert.Equal("cvText", ex.ParamName);
        Assert.StartsWith("CV chưa có nội dung nên không chấm được.", ex.Message);
    }
}
