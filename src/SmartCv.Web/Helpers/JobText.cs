using System.Globalization;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;

namespace SmartCv.Web.Helpers;

public static class JobText
{
    public static (string Text, string Css) Status(JobDto job) => !job.IsActive
        ? ("Ngừng tuyển", "is-inactive") : job.IsExpired ? ("Quá hạn", "is-expired") : ("Đang tuyển", "is-active");
    public static string Headcount(int? count) => count?.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")) ?? "Chưa xác định";
    public static string Closing(DateOnly? date) => date?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "Không giới hạn";
    public static string? Stage(string? value) => ApplicationStages.All.FirstOrDefault(s => string.Equals(s, value, StringComparison.OrdinalIgnoreCase));
    public static string ResultsUrl(int jobId, string? stage = null) => $"results?jobId={jobId}" + (Stage(stage) is { } valid ? $"&filter={valid}" : "");
    public static string ConfirmTitle(JobDto job) => job.IsActive ? "Ngừng tuyển vị trí này?" : "Mở lại vị trí tuyển dụng?";
    public static string ConfirmText(JobDto job) => job.IsActive
        ? $"Ngừng tuyển “{job.Title}” sẽ ẩn vị trí khỏi danh sách chọn khi tải CV. Hồ sơ và kết quả đã có vẫn được giữ nguyên."
        : $"Mở lại “{job.Title}” sẽ cho phép chọn vị trí khi tải CV."
            + (job.IsExpired ? " Hạn nhận CV đã qua; hãy cập nhật hạn nhận hồ sơ để tiếp tục tuyển dụng." : "");
}
