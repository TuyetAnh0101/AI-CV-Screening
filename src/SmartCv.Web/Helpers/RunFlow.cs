using System.Text.RegularExpressions;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Web.Models;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

/// <summary>Kết quả tổng của một lượt phân tích, dùng để chọn màu, biểu tượng và câu thông báo.</summary>
public enum RunOutcome { Queued, Running, Completed, AiFallback, PartialFailure, AllFailed, Failed }

/// <summary>
/// B6: phần logic thuần của nút "Phân tích hồ sơ" và thẻ tiến độ (không phụ thuộc giao diện, có unit test).
/// Luồng thật: POST /api/applications (A8) → applicationIds → POST /api/runs → runId → GET /api/runs/{id}.
/// </summary>
public static partial class RunFlow
{
    /// <summary>A8 nhận tối đa 100 CV mỗi lần gắn (xem ApplicationApiClient).</summary>
    public const int MaxCvsPerAnalysis = 100;

    /// <summary>CV được gửi đi phân tích: đã có CvId thật (Hoàn thành / Đã có sẵn) và đang được tích chọn.
    /// Dòng "Lỗi file", "Bị từ chối", "Đang tải lên" không bao giờ nằm trong danh sách này (B5).</summary>
    public static List<UploadItem> SelectedForAnalysis(IEnumerable<UploadItem> items) =>
        items.Where(i => i.Selected && i.CanAnalyze && i.CvId is { } id && id != Guid.Empty).ToList();

    /// <summary>Kiểm tra trước khi gọi API. Trả null nếu được phân tích.</summary>
    public static (bool JobMissing, string Message)? ValidateAnalyze(JobDto? job, IReadOnlyCollection<UploadItem> items)
    {
        if (job is null || job.JobId <= 0 || !job.IsActive)
            return (true, "Hãy chọn công việc trước khi phân tích.");

        if (items.Any(i => i.State == UploadItemState.Uploading))
            return (false, "Đang tải CV lên. Vui lòng chờ tải xong rồi phân tích.");

        var selected = SelectedForAnalysis(items).Count;
        if (selected == 0)
            return (false, items.Count == 0
                ? "Chưa có CV nào. Hãy kéo thả file CV vào ô phía trên."
                : "Chưa có CV hợp lệ nào được chọn. Hãy tích chọn ít nhất một CV có trạng thái \"Hoàn thành\" hoặc \"Đã có sẵn\".");

        if (selected > MaxCvsPerAnalysis)
            return (false, $"Mỗi lần chỉ phân tích tối đa {MaxCvsPerAnalysis} CV. Bạn đang chọn {selected} CV.");

        return null;
    }

    /// <summary>Lấy mã hồ sơ từ kết quả gắn CV (A8). CV "CvNotFound" không có ApplicationId nên bị bỏ và được đếm riêng.</summary>
    public static (List<int> ApplicationIds, int NotFound) ApplicationIdsFrom(CreateApplicationsResponse response)
    {
        var ids = response.Items
            .Where(i => i.Status is ApplicationItemStatuses.Created or ApplicationItemStatuses.AlreadyExists
                        && i.ApplicationId is > 0)
            .Select(i => i.ApplicationId!.Value)
            .Distinct()
            .ToList();
        return (ids, response.Items.Count(i => i.Status == ApplicationItemStatuses.CvNotFound));
    }

    /// <summary>Câu lỗi khi gọi API thất bại. Lỗi máy chủ (5xx) không hiện chi tiết kỹ thuật.</summary>
    public static string FailureMessage<T>(ApiResult<T> result, string serverError) =>
        result.StatusCode >= 500 ? serverError : result.ErrorMessage ?? serverError;

    public static bool IsFinished(string? status) => status is RunStatuses.Completed or RunStatuses.Failed;

    public static int Succeeded(RunProgressDto run) => Math.Max(0, run.ProcessedItems - run.FailedItems);

    public static int AiSucceeded(RunProgressDto run) => run.Items.Count(i =>
        i.Status == EvaluationStatuses.Completed && i.ScoreSource == ScoreSources.Combined && i.AiScore is not null);

    public static int KeywordOnly(RunProgressDto run) => run.Items.Count(i =>
        i.Status == EvaluationStatuses.Completed && i.ScoreSource == ScoreSources.KeywordOnly);

    public static int Pending(RunProgressDto run) => Math.Max(0, run.TotalItems - run.ProcessedItems);

    public static int Percent(RunProgressDto run) => (int)Math.Clamp(Math.Round(run.Percent), 0, 100);

    public static RunOutcome Outcome(RunProgressDto run) => run.Status switch
    {
        RunStatuses.Queued => RunOutcome.Queued,
        RunStatuses.Running => RunOutcome.Running,
        RunStatuses.Failed => RunOutcome.Failed,
        RunStatuses.Completed when run.TotalItems > 0 && run.FailedItems >= run.TotalItems => RunOutcome.AllFailed,
        RunStatuses.Completed when run.FailedItems > 0 => RunOutcome.PartialFailure,
        RunStatuses.Completed when KeywordOnly(run) > 0 => RunOutcome.AiFallback,
        RunStatuses.Completed => RunOutcome.Completed,
        _ => RunOutcome.Running   // trạng thái lạ: coi như chưa xong, vẫn hỏi tiếp
    };

    /// <summary>
    /// Lỗi từng hồ sơ hiển thị cho HR: bỏ phần kỹ thuật trong ngoặc mà máy chủ ghi kèm,
    /// ví dụ "(ExtractionStatus = Failed)" hay "(HttpRequestException)".
    /// </summary>
    public static string? FriendlyItemError(RunItemDto item)
    {
        if (item.Status == EvaluationStatuses.Completed)
            return item.ScoreSource == ScoreSources.KeywordOnly
                ? AiFailureReason(item.ErrorMessage) is { } reason
                    ? $"AI chưa chấm được ({reason}), điểm chỉ tính theo từ khóa."
                    : "AI chưa chấm được, điểm chỉ tính theo từ khóa."
                : null;

        if (string.IsNullOrWhiteSpace(item.ErrorMessage)) return null;
        var text = TechnicalParens().Replace(item.ErrorMessage, string.Empty).Trim();
        return text.Length == 0 ? "Không chấm được hồ sơ này." : text;
    }

    /// <summary>
    /// Nguyên nhân AI lỗi, ngắn gọn cho HR, suy từ ErrorMessage mà máy chủ lưu (GeminiEvaluationService).
    /// Không hiện chi tiết kỹ thuật tiếng Anh của Google. Không nhận ra thì trả null (dùng câu chung).
    /// </summary>
    public static string? AiFailureReason(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage)) return null;
        bool Has(string text) => errorMessage.Contains(text, StringComparison.OrdinalIgnoreCase);

        if (Has("HTTP 503") || Has("quá tải")) return "Gemini đang quá tải, có thể thử lại sau";
        if (Has("HTTP 404") || Has("Không tìm thấy model")) return "model Gemini không khả dụng, cần kiểm tra cấu hình model";
        if (Has("HTTP 429") || Has("vượt hạn mức")) return "đã vượt hạn mức gọi Gemini";
        if (Has("không phản hồi sau")) return "Gemini không phản hồi kịp";
        if (Has("API key") || Has("Không có quyền")) return "lỗi cấu hình API key Gemini";
        if (Has("JSON") || Has("trả về rỗng")) return "Gemini trả kết quả không hợp lệ";
        if (Has("từ chối nội dung") || Has("dừng trả lời")) return "Gemini từ chối xử lý nội dung CV";
        if (Has("Không kết nối được Gemini") || Has("lỗi tạm thời")) return "không kết nối được Gemini";
        return null;
    }

    [GeneratedRegex(@"\s*\((?:[A-Za-z]+\s*=\s*\w+|[A-Za-z.]*Exception)\)")]
    private static partial Regex TechnicalParens();
}
