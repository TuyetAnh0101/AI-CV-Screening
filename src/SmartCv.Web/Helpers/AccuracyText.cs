using System.Globalization;
using SmartCv.Core.Contracts;

namespace SmartCv.Web.Helpers;

/// <summary>
/// B41: chữ hiển thị tỷ lệ đúng kèm khoảng tin cậy 95% (Wilson) mà API A41 đã tính, ví dụ "70% (40–89%)".
/// Web không tự tính khoảng tin cậy: chỉ đọc AccuracyLow/AccuracyHigh và bỏ qua khoảng không hợp lệ thay vì đoán.
/// </summary>
public static class AccuracyText
{
    /// <summary>Câu bắt buộc của guide khi hai khoảng tin cậy chồng nhau (A41 Conclusive = false).</summary>
    public const string Inconclusive = "Chưa đủ dữ liệu để kết luận bản nào tốt hơn, cần thêm CV có nhãn";

    // API làm tròn tỷ lệ và hai đầu khoảng tới 2 chữ số: cho phép lệch 0,01 điểm khi kiểm tra tỷ lệ nằm trong khoảng.
    private const decimal Tolerance = 0.01m;
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    /// <summary>"66,67%" (vi-VN, tối đa 2 chữ số thập phân như bảng B15). null → "—", không bao giờ thành 0%.</summary>
    public static string Percent(decimal? value) => value is null ? "—" : value.Value.ToString("0.##", Vi) + "%";

    /// <summary>Khoảng hợp lệ: có đủ hai đầu, 0 ≤ thấp ≤ cao ≤ 100 và (nếu có) tỷ lệ nằm trong khoảng.</summary>
    public static bool HasRange(decimal? accuracy, decimal? low, decimal? high) =>
        low is { } l && high is { } h && l >= 0 && h <= 100 && l <= h
        && (accuracy is not { } a || (a >= l - Tolerance && a <= h + Tolerance));

    /// <summary>"40–89%" (làm tròn số nguyên). Thiếu hoặc sai khoảng → "".</summary>
    public static string Range(decimal? low, decimal? high) =>
        HasRange(null, low, high) ? $"{Whole(low!.Value)}–{Whole(high!.Value)}%" : "";

    /// <summary>"70% (40–89%)"; khoảng thực sự null/không hợp lệ → "70%"; không có tỷ lệ → "—".</summary>
    public static string WithRange(decimal? accuracy, decimal? low, decimal? high) =>
        accuracy is null ? "—" : HasRange(accuracy, low, high) ? $"{Percent(accuracy)} ({Range(low, high)})" : Percent(accuracy);

    /// <summary>Câu đầy đủ cho trình đọc màn hình (không dựa vào ký hiệu hay màu).</summary>
    public static string Describe(decimal? accuracy, decimal? low, decimal? high) =>
        accuracy is null ? "Chưa có tỷ lệ đúng"
        : HasRange(accuracy, low, high)
            ? $"Tỷ lệ đúng {Percent(accuracy)}, khoảng tin cậy 95% từ {Whole(low!.Value)}% đến {Whole(high!.Value)}%"
            : $"Tỷ lệ đúng {Percent(accuracy)}, chưa có khoảng tin cậy";

    /// <summary>Độ rộng khoảng (điểm phần trăm) để giải thích độ chắc chắn; null khi khoảng không hợp lệ.</summary>
    public static decimal? Width(decimal? low, decimal? high) => HasRange(null, low, high) ? high!.Value - low!.Value : null;

    /// <summary>Hai khoảng chồng nhau (chạm nhau cũng tính là chồng, giống A41). Thiếu một khoảng → false.</summary>
    public static bool Overlap(decimal? lowA, decimal? highA, decimal? lowB, decimal? highB) =>
        HasRange(null, lowA, highA) && HasRange(null, lowB, highB) && !(highA < lowB || highB < lowA);

    /// <summary>
    /// null khi PromptTestRunDto.Matrix khớp các bộ đếm của chính lượt đó; ngược lại câu giải thích vì sao không hiển thị.
    /// Web không tự tính ma trận từ số tổng.
    /// </summary>
    public static string? MatrixIssue(PromptTestRunDto run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Matrix is not { } m)
            return $"API chưa trả ma trận 2×2 cho lượt #{run.RunId} (cần A41). Không tự tính ma trận từ số tổng.";
        var consistent = m.TruePass >= 0 && m.Missed >= 0 && m.WrongPick >= 0 && m.TrueFail >= 0 && m.Unscored >= 0
            && (long)m.TruePass + m.TrueFail == run.CorrectCount && m.Missed == run.MissedCount && m.WrongPick == run.WrongPickCount
            && m.Unscored == run.UnscoredCount && (long)m.TruePass + m.TrueFail + m.Missed + m.WrongPick + m.Unscored == run.TotalLabeledCount;
        return consistent ? null
            : $"Ma trận của lượt #{run.RunId} không khớp số Đúng/Bỏ sót/Chọn nhầm của bảng. Không hiển thị để tránh hiểu sai; hãy làm mới so sánh.";
    }

    /// <summary>Vị trí CSS (%, dấu chấm) cho thanh khoảng tin cậy, luôn trong 0–100.</summary>
    public static string Css(decimal value) => Math.Clamp(value, 0, 100).ToString("0.##", CultureInfo.InvariantCulture);

    private static string Whole(decimal value) => Math.Round(value, 0, MidpointRounding.AwayFromZero).ToString("0", Vi);
}
