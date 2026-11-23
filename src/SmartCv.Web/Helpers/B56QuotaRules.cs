using System.Globalization;

namespace SmartCv.Web.Helpers;

/// <summary>B56: One source of truth for workspace quota and missing-prompt notices.</summary>
public static class B56QuotaRules
{
    public const string AiExceeded = "ai_quota_exceeded";
    public const string CvExceeded = "cv_quota_exceeded";
    public const string NoActivePrompt = "no_active_prompt";

    public static bool Recognized(int? status, string? code) => (status, code) switch
    {
        (429, AiExceeded) => true,
        (409, CvExceeded) => true,
        (409, NoActivePrompt) => true,
        _ => false
    };

    public static string Message(int? status, string? code, int? used = null, int? limit = null,
        DateTimeOffset? resetsAt = null) => (status, code) switch
    {
        (429, AiExceeded) => "Đã hết lượt phân tích AI hôm nay" +
            (used is >= 0 && limit is > 0 ? $" ({used}/{limit})" : "") + ". " +
            (resetsAt is { } reset ? $"Thử lại sau {VietnamClock(reset)}." : "Vui lòng thử lại sau khi hạn mức được đặt lại."),
        (409, CvExceeded) => limit is > 0
            ? $"Không gian dùng thử giới hạn {limit} CV. Xóa bớt CV cũ để tải thêm."
            : "Đã đạt giới hạn lưu CV của không gian làm việc. Xóa bớt CV cũ để tải thêm.",
        (409, NoActivePrompt) => "Chưa có System Prompt đang hoạt động. Hãy mở System Prompt và kích hoạt một phiên bản trước khi phân tích.",
        _ => ""
    };

    public static string VietnamClock(DateTimeOffset reset) =>
        reset.ToOffset(TimeSpan.FromHours(7)).ToString("HH:mm 'ngày' dd/MM/yyyy", CultureInfo.GetCultureInfo("vi-VN"));
}
