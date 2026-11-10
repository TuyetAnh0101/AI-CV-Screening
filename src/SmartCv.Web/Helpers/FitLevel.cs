using SmartCv.Core.Constants;

namespace SmartCv.Web.Helpers;

/// <summary>Kết quả đối chiếu nhãn mức của API với điểm cuối (B8).</summary>
public enum FitLabelIssue
{
    /// <summary>Nhãn của API hợp lệ và khớp quy tắc điểm.</summary>
    None,
    /// <summary>API không gửi nhãn nhưng có điểm: giao diện tính mức theo điểm (đánh dấu "tính theo điểm").</summary>
    DerivedFromScore,
    /// <summary>Nhãn của API là một trong 4 mức nhưng KHÁC mức tính từ điểm cuối. Vẫn hiện nhãn của API, kèm cảnh báo.</summary>
    Mismatch,
    /// <summary>Nhãn của API không thuộc 4 mức: hiện "Mức không xác định", không đoán.</summary>
    Unknown
}

/// <summary>Cột "Bắt buộc" của bảng xếp hạng (B8): đủ → xanh, thiếu → cam. Số liệu lấy nguyên từ API, không suy đoán.</summary>
public sealed record MustHaveDisplay(string Text, string Detail, string CssClass, int Matched, int Total)
{
    /// <summary>Tối đa 8 chấm; nhiều hơn chỉ hiện chữ để không vỡ cột.</summary>
    public const int MaxDots = 8;
    public bool ShowDots => Total is > 0 and <= MaxDots;

    /// <summary>null khi CV chưa có điểm (đang chấm/lỗi): API chưa có kết quả kỹ năng, hiện "—".</summary>
    public static MustHaveDisplay? From(int matched, int total, bool scored)
    {
        if (!scored) return null;
        if (total <= 0) return new("Không yêu cầu", "Vị trí không có kỹ năng bắt buộc", "must-none", 0, 0);
        return matched >= total
            ? new($"{matched}/{total}", "Đủ kỹ năng bắt buộc", "must-ok", matched, total)
            : new($"{matched}/{total}", $"Thiếu {total - Math.Max(matched, 0)} kỹ năng bắt buộc", "must-missing", matched, total);
    }
}

/// <summary>Nhãn mức sẽ hiển thị + lớp CSS + vấn đề dữ liệu (nếu có).</summary>
public sealed record FitDisplay(string Label, string CssClass, FitLabelIssue Issue, string? ExpectedLabel = null)
{
    public bool HasIssue => Issue is FitLabelIssue.Mismatch or FitLabelIssue.Unknown;
}

/// <summary>
/// 4 mức đánh giá (tài liệu tổng quan, mục 5) — CÙNG quy tắc với Backend (SmartCv.Core.Scoring.FitLevels):
///   điểm ≥ 85 Rất phù hợp · 70 ≤ điểm &lt; 85 Phù hợp · 55 ≤ điểm &lt; 70 Tiềm năng · điểm &lt; 55 Chưa phù hợp.
/// Không làm tròn trước khi phân loại (84,99 vẫn là Phù hợp). Không có điểm (null) → không có mức, KHÔNG coi là 0.
/// B8: mọi chỗ tô màu theo mức (FitBadge, ScoreBar, nhãn của B6) đều đi qua lớp này.
/// Màu khai báo trong wwwroot/app.css: --fit-excellent / --fit-good / --fit-potential / --fit-poor (+ -bg).
/// </summary>
public static class FitLevel
{
    public const string Excellent = "Rất phù hợp";
    public const string Good = "Phù hợp";
    public const string Potential = "Tiềm năng";
    public const string Poor = "Chưa phù hợp";

    public const decimal ExcellentFrom = 85;
    public const decimal GoodFrom = 70;
    public const decimal PotentialFrom = 55;

    public static readonly IReadOnlyList<string> All = [Excellent, Good, Potential, Poor];

    public static string FromScore(decimal? score) => score switch
    {
        null => string.Empty,
        >= ExcellentFrom => Excellent,
        >= GoodFrom => Good,
        >= PotentialFrom => Potential,
        _ => Poor
    };

    public static bool IsKnown(string? label) => label is Excellent or Good or Potential or Poor;

    /// <summary>Tên lớp CSS: fit-excellent | fit-good | fit-potential | fit-poor | fit-none</summary>
    public static string CssClass(string? label) => label switch
    {
        Excellent => "fit-excellent",
        Good => "fit-good",
        Potential => "fit-potential",
        Poor => "fit-poor",
        _ => "fit-none"
    };

    /// <summary>Khoảng điểm của mức (chú giải, tooltip).</summary>
    public static string Range(string? label) => label switch
    {
        Excellent => "85–100",
        Good => "70–84",
        Potential => "55–69",
        Poor => "0–54",
        _ => string.Empty
    };

    /// <summary>
    /// Chọn nhãn hiển thị. Ưu tiên nhãn của API (Backend đã tính, kể cả khi điểm bị giới hạn vì thiếu kỹ năng bắt buộc);
    /// chỉ đối chiếu để BÁO vấn đề dữ liệu, không âm thầm đổi nhãn. Trả null khi chưa có điểm lẫn nhãn.
    /// </summary>
    public static FitDisplay? Resolve(string? apiLabel, decimal? finalScore)
    {
        var expected = FromScore(finalScore);
        if (string.IsNullOrWhiteSpace(apiLabel))
            return finalScore is null ? null : new FitDisplay(expected, CssClass(expected), FitLabelIssue.DerivedFromScore);

        var label = apiLabel.Trim();
        if (!IsKnown(label))
            return new FitDisplay("Mức không xác định", "fit-none", FitLabelIssue.Unknown, expected.Length == 0 ? null : expected);

        return finalScore is not null && expected != label
            ? new FitDisplay(label, CssClass(label), FitLabelIssue.Mismatch, expected)
            : new FitDisplay(label, CssClass(label), FitLabelIssue.None);
    }

    /// <summary>Câu giải thích cho tooltip khi nhãn có vấn đề.</summary>
    public static string? IssueText(FitDisplay? fit, string? apiLabel) => fit?.Issue switch
    {
        FitLabelIssue.DerivedFromScore => "API chưa gửi nhãn mức, giao diện tính theo điểm cuối.",
        FitLabelIssue.Mismatch => $"Dữ liệu cần kiểm tra: API ghi \"{apiLabel}\" nhưng điểm cuối thuộc mức \"{fit.ExpectedLabel}\".",
        FitLabelIssue.Unknown => $"Dữ liệu cần kiểm tra: API trả nhãn mức không hợp lệ \"{apiLabel}\".",
        _ => null
    };

    /// <summary>
    /// Vị trí trên thanh điểm (0–100%) — CHỈ để vẽ: giá trị ngoài 0–100 bị kẹp cho khỏi tràn thanh,
    /// điểm gốc của API vẫn hiển thị nguyên văn. null → null (không vẽ lớp đó, không coi là 0).
    /// </summary>
    public static decimal? BarPercent(decimal? value) => value is null ? null : Math.Clamp(value.Value, 0m, 100m);

    /// <summary>Mô tả đầy đủ cho trình đọc màn hình: hiểu được thanh điểm mà không cần nhìn màu.</summary>
    public static string ScoreBarLabel(decimal? keyword, decimal? final, decimal? threshold, string? scoreSource)
    {
        if (final is null && keyword is null) return "Chưa có điểm";
        var parts = new List<string>();
        if (final is not null)
        {
            var label = FromScore(final);
            parts.Add($"Điểm cuối {RankingView.Score(final)} ({label})");
        }
        else
        {
            parts.Add("Chưa có điểm cuối");
        }
        parts.Add(keyword is null ? "chưa có điểm keyword" : $"điểm keyword {RankingView.Score(keyword)}");
        if (scoreSource == ScoreSources.KeywordOnly) parts.Add("chỉ chấm keyword, AI chưa chấm được");
        if (threshold is not null)
        {
            parts.Add(final is null
                ? $"ngưỡng Đạt {RankingView.Score(threshold)}"
                : $"ngưỡng Đạt {RankingView.Score(threshold)}: {(final >= threshold ? "đạt ngưỡng" : "chưa đạt ngưỡng")}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>Tông màu của thanh điểm (B8): theo mức của điểm cuối; chỉ keyword → xám trung tính; chưa có điểm → không tô.</summary>
    public static string ScoreTone(decimal? finalScore, string? scoreSource) =>
        finalScore is null ? "tone-none"
        : scoreSource == ScoreSources.KeywordOnly ? "tone-keyword"
        : CssClass(FromScore(finalScore)).Replace("fit-", "tone-", StringComparison.Ordinal);
}
