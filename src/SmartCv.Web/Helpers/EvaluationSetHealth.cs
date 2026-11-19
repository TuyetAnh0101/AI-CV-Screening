using System.Globalization;
using SmartCv.Core.Evaluation;

namespace SmartCv.Web.Helpers;

public enum SetHealthLevel { Empty, Poor, Caution, Good }

public enum IssueSeverity { Info, Caution, Severe }

public sealed record SetIssue(IssueSeverity Severity, string Text);

/// <summary>B25: một bộ đánh giá đã đủ tin cậy để so sánh prompt chưa.</summary>
public sealed record SetHealth(
    SetHealthLevel Level,
    int Total,
    int Pass,
    int Fail,
    decimal? MinorityPercent,
    decimal? MarginOfError,
    IReadOnlyList<SetIssue> Issues)
{
    public string LevelText => Level switch
    {
        SetHealthLevel.Good => "Đủ tin cậy",
        SetHealthLevel.Caution => "Dùng được, cần thận trọng",
        SetHealthLevel.Poor => "Chưa đủ tin cậy",
        _ => "Chưa gán nhãn"
    };

    public string LevelCss => Level switch
    {
        SetHealthLevel.Good => "good",
        SetHealthLevel.Caution => "caution",
        SetHealthLevel.Poor => "poor",
        _ => "empty"
    };
}

/// <summary>
/// B25 (theo hướng dẫn C8 – cách xây bộ đánh giá). Hàm thuần, chỉ đọc số liệu API đã trả; không gọi API, không đổi nhãn.
///   - Cỡ: dưới 30 CV có nhãn → quá nhỏ; 30–49 → tạm dùng; từ 50 → tốt.
///   - Cân bằng: nhãn ít hơn dưới 20% → lệch nặng; dưới 30% → hơi lệch.
///   - Mỗi nhãn nên có ít nhất 10 CV, nếu không số bỏ sót / chọn nhầm khó tin cậy.
///   - CV có nhãn nhưng chưa đọc được chữ → AI không chấm đúng được.
///   - Chỉ 1 vị trí → chỉ là thông tin, không hạ mức.
///   - Sai số: nửa độ rộng khoảng tin cậy 95% (Wilson, A41) khi prompt đúng khoảng 80% trên bộ này.
/// </summary>
public static class EvaluationSetHealth
{
    public const int MinSize = 30;
    public const int GoodSize = 50;
    public const decimal SevereSkewPercent = 20;
    public const decimal SkewPercent = 30;
    public const int MinPerLabel = 10;
    public const double TypicalAccuracy = 0.8;

    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    /// <param name="labelCount">Số CV có nhãn HR (Đạt hoặc Không đạt).</param>
    /// <param name="passCount">Số nhãn Đạt.</param>
    /// <param name="jobCount">Số vị trí khác nhau của các CV có nhãn; 0 = chưa biết (không kiểm tra).</param>
    /// <param name="failedExtractionCount">Số CV có nhãn nhưng chưa có chữ dùng được.</param>
    public static SetHealth Assess(int labelCount, int passCount, int jobCount = 0, int failedExtractionCount = 0)
    {
        var total = Math.Max(0, labelCount);
        var pass = Math.Clamp(passCount, 0, total);
        var fail = total - pass;
        if (total == 0)
            return new SetHealth(SetHealthLevel.Empty, 0, 0, 0, null, null,
                [new SetIssue(IssueSeverity.Severe, "Chưa có CV nào được HR gán nhãn Đạt / Không đạt.")]);

        var issues = new List<SetIssue>();

        if (total < MinSize)
            issues.Add(new(IssueSeverity.Severe, $"Quá nhỏ: {total} CV có nhãn (cần ít nhất {MinSize}). Hai prompt chênh 10% vẫn có thể chỉ là ngẫu nhiên."));
        else if (total < GoodSize)
            issues.Add(new(IssueSeverity.Caution, $"Tạm đủ: {total} CV có nhãn. Nên thêm tới {GoodSize} CV để so sánh chắc hơn."));

        var minority = Math.Min(pass, fail);
        var minorityPercent = Math.Round(100m * minority / total, 1);
        var minorityName = pass <= fail ? "Đạt" : "Không đạt";
        var percentText = minorityPercent.ToString("0.#", Vi);
        if (minorityPercent < SevereSkewPercent)
            issues.Add(new(IssueSeverity.Severe,
                $"Lệch nhãn nặng: chỉ {percentText}% {minorityName}. Prompt đoán tất cả một nhãn vẫn có tỷ lệ đúng cao."));
        else if (minorityPercent < SkewPercent)
            issues.Add(new(IssueSeverity.Caution, $"Hơi lệch nhãn: {percentText}% {minorityName} (nên từ {SkewPercent:0}% trở lên)."));

        if (minority < MinPerLabel && total >= MinSize)
            issues.Add(new(IssueSeverity.Caution,
                $"Chỉ {minority} CV {minorityName}: số {(minorityName == "Đạt" ? "bỏ sót" : "chọn nhầm")} khó tin cậy (nên từ {MinPerLabel} CV)."));

        if (failedExtractionCount > 0)
            issues.Add(new(IssueSeverity.Caution,
                $"{failedExtractionCount} CV có nhãn nhưng chưa đọc được chữ: AI không chấm đúng được. Nên bỏ khỏi bộ hoặc đọc lại CV."));

        if (jobCount == 1)
            issues.Add(new(IssueSeverity.Info, "Chỉ có CV của 1 vị trí: kết quả so sánh chỉ đúng cho vị trí này."));

        var level = issues.Any(i => i.Severity == IssueSeverity.Severe) ? SetHealthLevel.Poor
                  : issues.Any(i => i.Severity == IssueSeverity.Caution) ? SetHealthLevel.Caution
                  : SetHealthLevel.Good;

        return new SetHealth(level, total, pass, fail, minorityPercent, MarginOfError(total), issues);
    }

    /// <summary>"±10,9 điểm %": nếu prompt đúng khoảng 80% trên bộ n CV, tỷ lệ thật nằm trong khoảng này (95%).</summary>
    public static decimal MarginOfError(int n)
    {
        if (n <= 0) return 100;
        var k = (int)Math.Round(TypicalAccuracy * n, MidpointRounding.AwayFromZero);
        var (low, high) = PromptAccuracyCalculator.Wilson(k, n);
        return Math.Round((high - low) / 2, 1, MidpointRounding.AwayFromZero);
    }
}
