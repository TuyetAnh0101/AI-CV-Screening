using System.Globalization;
using SmartCv.Core.Constants;
using SmartCv.Core.Runs;

namespace SmartCv.Web.Helpers;

/// <summary>
/// B26: presentation text for run history (/runs) and run detail (/runs/{id}).
/// The B19 guide planned this file, but main did not contain it, so B26 owns it. Never changes API values.
/// </summary>
public static class RunText
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    public const string FailedKind = "Failed";
    public const string KeywordOnlyKind = "KeywordOnly";

    public static string Type(string? type) => type switch
    {
        RunTypes.Screening => "Sàng lọc",
        RunTypes.PromptTest => "Đánh giá prompt",
        _ => "Không rõ loại"
    };

    public static bool IsPromptTest(string? type) => type == RunTypes.PromptTest;

    public static bool IsActive(string? status) => status is RunStatuses.Queued or RunStatuses.Running;

    public static string Number(long value) => value.ToString("N0", Vi);

    /// <summary>API times are UTC (Unspecified is read as UTC). Null when missing or not representable in UTC+7.</summary>
    public static DateTimeOffset? Vietnam(DateTime? value)
    {
        if (value is not { } v || v == default) return null;
        var utc = v.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : v.ToUniversalTime();
        if (utc > DateTime.MaxValue.AddHours(-7)) return null;
        return new DateTimeOffset(utc).ToOffset(TimeSpan.FromHours(7));
    }

    public static string VnDate(DateTime? value) => Vietnam(value)?.ToString("dd/MM/yyyy", Vi) ?? "—";

    public static string VnClock(DateTime? value) => Vietnam(value)?.ToString("HH:mm", Vi) ?? "";

    public static string VnDateTime(DateTime? value) => Vietnam(value)?.ToString("dd/MM/yyyy HH:mm", Vi) ?? "—";

    /// <summary>Machine-readable value for &lt;time datetime&gt;.</summary>
    public static string? IsoTime(DateTime? value) => Vietnam(value)?.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>Elapsed seconds: null = unknown ("—"), 0 = finished in under one second. Never shows a negative duration.</summary>
    public static string Duration(long? seconds) => seconds switch
    {
        null or < 0 => "—",
        0 => "Dưới 1 giây",
        < 60 => $"{seconds} giây",
        < 3600 => seconds % 60 == 0 ? $"{seconds / 60} phút" : $"{seconds / 60} phút {seconds % 60} giây",
        < 86400 => seconds % 3600 / 60 == 0 ? $"{seconds / 3600} giờ" : $"{seconds / 3600} giờ {seconds % 3600 / 60} phút",
        _ => seconds % 86400 / 3600 == 0 ? $"{Number(seconds.Value / 86400)} ngày" : $"{Number(seconds.Value / 86400)} ngày {seconds % 86400 / 3600} giờ"
    };

    /// <summary>A queued/running run never reads as finished, even when the API already sent a duration.</summary>
    public static string RunDuration(string? status, long? seconds) => status switch
    {
        RunStatuses.Queued => "Chưa bắt đầu chấm",
        RunStatuses.Running => "Đang chạy",
        _ => seconds is null ? "Chưa ghi nhận" : Duration(seconds)
    };

    /// <summary>Same clamp rule as A25 (RunHistoryQuery.DurationSeconds), for GET /api/runs/{id} which has no duration field.</summary>
    public static int? DurationSeconds(DateTime createdAt, DateTime? completedAt) =>
        RunHistoryQuery.DurationSeconds(createdAt, completedAt);

    /// <summary>Weights stored as fractions (0,3 / 0,7): "30% từ khóa · 70% AI".</summary>
    public static string Weights(decimal keyword, decimal ai) => $"{Display.Weight(keyword)} từ khóa · {Display.Weight(ai)} AI";

    /// <summary>Width of the keyword share in a weight bar, invariant CSS percentage. Null when the weights cannot be drawn.</summary>
    public static string? KeywordShare(decimal keyword, decimal ai)
    {
        if (keyword < 0 || ai < 0 || keyword + ai <= 0) return null;
        return Math.Round(keyword / (keyword + ai) * 100m, 2).ToString("0.##", CultureInfo.InvariantCulture);
    }

    public static string Temperature(decimal value) => value.ToString("0.0#", Vi);

    public static string Threshold(decimal value) => Display.Score(value) + " điểm";

    /// <summary>Null stays "—": a run without accuracy is not a 0% run.</summary>
    public static string Accuracy(decimal? percent) => percent is null ? "—" : percent.Value.ToString("0.#", Vi) + "%";

    public static string FailureKind(string? kind) => kind switch
    {
        FailedKind => "Không chấm được",
        KeywordOnlyKind => "AI lỗi · chỉ có điểm từ khóa",
        _ => "Lỗi khác"
    };

    /// <summary>"Trang 2/5 · 87 lượt".</summary>
    public static string PageInfo(int page, int totalPages, int totalCount) =>
        $"Trang {Number(page)}/{Number(Math.Max(1, totalPages))} · {Number(totalCount)} lượt";

    // A25 RunEndpoints.SafeRunFailure returns only these controlled reasons (no CV text, paths, URLs or keys).
    private static readonly HashSet<string> KnownReasons = new(StringComparer.Ordinal)
    {
        "Thông tin lỗi đã được ẩn để bảo vệ hồ sơ đã ẩn danh.",
        "Không đọc được nội dung CV. Hãy kiểm tra và tải lại tệp rõ hơn.",
        "CV chưa trích xuất xong nội dung nên chưa thể chấm.",
        "Không chấm được hồ sơ. Dịch vụ AI đã hết hạn mức hoặc đang giới hạn tần suất.",
        "Chỉ tính điểm từ khóa. Dịch vụ AI đã hết hạn mức hoặc đang giới hạn tần suất.",
        "Không chấm được hồ sơ. Dịch vụ AI đang quá tải.",
        "Chỉ tính điểm từ khóa. Dịch vụ AI đang quá tải.",
        "Không chấm được hồ sơ. Dịch vụ AI phản hồi quá thời gian chờ.",
        "Chỉ tính điểm từ khóa. Dịch vụ AI phản hồi quá thời gian chờ.",
        "Không chấm được hồ sơ. Vui lòng kiểm tra trạng thái CV và cấu hình chấm.",
        "AI chưa chấm thành công; chỉ tính điểm từ khóa."
    };

    /// <summary>
    /// Failure reason safe to render even with Ẩn danh on: only A25's fixed sentences pass through.
    /// Anything else (raw provider error, path, key, CV excerpt) is replaced by a generic sentence of the same kind.
    /// </summary>
    public static string SafeReason(string? kind, string? message) =>
        message is not null && KnownReasons.Contains(message) ? message
        : kind == FailedKind ? "Không chấm được hồ sơ. Vui lòng kiểm tra trạng thái CV và cấu hình chấm."
        : "AI chưa chấm thành công; chỉ tính điểm từ khóa.";

    /// <summary>Extraction guidance is derived from the status only; the free-text extraction error is never rendered.</summary>
    public static string? CvHint(string? extractionStatus) => extractionStatus switch
    {
        ExtractionStatuses.Failed => "CV không đọc được chữ. Hãy tải lại tệp rõ nét hơn (PDF có chữ chọn được, hoặc ảnh chụp thẳng, đủ sáng) rồi chấm lại.",
        ExtractionStatuses.Pending => "CV chưa trích xuất xong nội dung. Chờ trích xuất hoàn tất rồi chấm lại.",
        _ => null
    };
}
