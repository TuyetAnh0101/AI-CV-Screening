using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;

namespace SmartCv.Web.Helpers;

public static class CvLibraryText
{
    public static readonly (string Code, string Name)[] Statuses =
        [("Completed", "Đã đọc chữ"), ("Pending", "Đang chờ đọc"), ("Failed", "Đọc lỗi"), ("Anonymized", "Đã ẩn danh")];

    public static string ShortId(Guid id) => id.ToString("N")[..8];
    public static string Candidate(CvListItemDto cv, bool anonymous) => cv.IsAnonymized ? "Hồ sơ đã ẩn danh"
        : anonymous ? $"Ứng viên · CV {ShortId(cv.CvId)}"
        : cv.CandidateId is null ? "Chưa gắn ứng viên" : Display.Text(cv.CandidateName);
    public static string File(CvListItemDto cv, bool anonymous) => cv.IsAnonymized ? "Hồ sơ đã ẩn danh"
        : anonymous ? "CV đã ẩn tên" : Display.Text(cv.FileName);
    public static string Source(string? code) => code == "JobBoard" ? "Trang tuyển dụng" : code is null ? "Chưa ghi" : CvSourceChannels.DisplayName(code);
    public static string Status(CvListItemDto cv) => cv.IsAnonymized ? "Đã ẩn danh"
        : Statuses.FirstOrDefault(s => s.Code == cv.ExtractionStatus).Name ?? "Chưa xác định";
    public static string StatusCss(CvListItemDto cv) => cv.IsAnonymized ? "muted" : cv.ExtractionStatus switch
        { "Completed" => "complete", "Failed" => "failed", "Pending" => "pending", _ => "muted" };
    public static string Stage(string? code) => ApplicationStages.IsValid(code) ? StageText.Name(code) : "—";
    // Unknown/free-text reasons must not reach tooltips in privacy mode.
    public static string? Reason(string? code, bool anonymous) => anonymous || !RejectReasons.IsValid(code) ? null : StageText.Reason(code);
    public static string Retention(DateTime? until, DateTime? anonymized, DateTime now)
    {
        if (anonymized is not null) return "Đã ẩn danh";
        if (until is null || RunText.Vietnam(until) is null || RunText.Vietnam(now) is null) return "—";
        var days = VnDate(until.Value).DayNumber - VnDate(now).DayNumber;
        var suffix = days < 0 ? $"quá hạn {-days} ngày" : days == 0 ? "hết hạn hôm nay" : $"còn {days} ngày";
        return $"{VnDate(until.Value):dd/MM/yyyy} · {suffix}";
    }
    public static bool RetentionWarning(CvListItemDto cv, DateTime now) => !cv.IsAnonymized && cv.RetainUntil is { } date && RunText.Vietnam(date) is not null && RunText.Vietnam(now) is not null
        && VnDate(date).DayNumber - VnDate(now).DayNumber <= 14;
    private static DateOnly VnDate(DateTime date) => DateOnly.FromDateTime(DateTime.SpecifyKind(date, DateTimeKind.Utc).AddHours(7));

    /// <summary>Fixed text only. Never display a server exception or echo submitted personal data.</summary>
    public static string Error(int? status, bool writing = false) => status switch
    {
        400 => "Máy chủ không chấp nhận dữ liệu. Hãy kiểm tra thông tin rồi thử lại.",
        404 => "Không tìm thấy dữ liệu hoặc API chưa hỗ trợ chức năng này. Hãy làm mới và thử lại.",
        409 or 410 => "Hồ sơ đã thay đổi hoặc đã được ẩn danh hóa. Hãy đóng hộp thoại và làm mới danh sách.",
        429 => "Đang có nhiều yêu cầu. Vui lòng chờ một lát rồi thử lại.",
        503 => "Máy chủ API tạm thời không sẵn sàng. Vui lòng thử lại sau ít phút.",
        >= 500 => "Máy chủ gặp lỗi khi xử lý hồ sơ. Vui lòng thử lại sau.",
        null when writing => "Chưa xác nhận được kết quả lưu. Hãy đóng hộp thoại và làm mới để kiểm tra trước khi gửi lại.",
        null => "Không kết nối được API hoặc đã quá thời gian chờ. Vui lòng thử lại.",
        _ => "Chưa xử lý được yêu cầu. Vui lòng thử lại."
    };
}
