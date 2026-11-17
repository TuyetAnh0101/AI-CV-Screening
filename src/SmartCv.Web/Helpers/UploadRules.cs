using System.Globalization;
using System.Text.RegularExpressions;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Extraction;
using SmartCv.Core.Scoring;
using SmartCv.Web.Models;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

/// <summary>B38: nhóm hiển thị của dòng "chữ đã đọc". Luôn đi kèm biểu tượng và chữ, không chỉ dựa vào màu.</summary>
public enum TextTone { None, Reading, Good, Review, Failed, Muted }

/// <summary>
/// B38: cách hiển thị trạng thái chữ đã đọc của một dòng.
/// <paramref name="Action"/>: chữ của nút mở hộp "Chữ đã đọc" ngay trên dòng (null: chỉ mở qua menu ⋯).
/// </summary>
public sealed record TextStatusView(TextTone Tone, string Label, string? Detail, string? Action);

/// <summary>
/// B38: quy tắc của trang Tải CV, tách khỏi .razor để có unit test.
/// Nguồn + đồng ý (A38), CV trùng (A42 existing*), chất lượng chữ (A42 textQuality), ước tính thời gian (A42 estimate).
/// Hàm thuần: không gọi API, không ghi log, không giữ nội dung CV.
/// </summary>
public static partial class UploadRules
{
    public const string ConsentText = "Ứng viên đã đồng ý cho xử lý dữ liệu cho mục đích tuyển dụng";
    public const string LockedTitle = "Chưa mở tải lên";

    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    // =====================================================================
    // Nguồn CV + xác nhận đồng ý (A38)
    // =====================================================================

    /// <summary>Đúng 5 mã hợp đồng A38, nhãn tiếng Việt lấy từ Core (một nguồn sự thật với API).</summary>
    public static IReadOnlyList<(string Code, string Label)> SourceOptions { get; } =
        CvSourceChannels.All.Select(c => (c, CvSourceChannels.DisplayName(c))).ToList();

    /// <summary>Ô kéo thả chỉ mở khi đã chọn một trong 5 nguồn và đã tích xác nhận đồng ý.</summary>
    public static bool CanUpload(string? sourceChannel, bool consentConfirmed) =>
        consentConfirmed && CvSourceChannels.IsValid(sourceChannel);

    /// <summary>Lý do ô kéo thả đang khóa, nói rõ còn thiếu gì (null: đã mở).</summary>
    public static string? LockReason(string? sourceChannel, bool consentConfirmed) =>
        (CvSourceChannels.IsValid(sourceChannel), consentConfirmed) switch
        {
            (true, true) => null,
            (false, true) => "Chọn nguồn CV trước khi tải lên.",
            (true, false) => "Tích xác nhận ứng viên đã đồng ý trước khi tải lên.",
            _ => "Chọn nguồn CV và tích xác nhận ứng viên đã đồng ý trước khi tải lên."
        };

    /// <summary>Số mục còn thiếu (0–2) cho nhãn trạng thái của khung "Nguồn hồ sơ và sự đồng ý".</summary>
    public static int MissingCount(string? sourceChannel, bool consentConfirmed) =>
        (CvSourceChannels.IsValid(sourceChannel) ? 0 : 1) + (consentConfirmed ? 0 : 1);

    // =====================================================================
    // Kết quả tải lên: lỗi cả request, CV trùng (A42)
    // =====================================================================

    /// <summary>
    /// Khung báo lỗi khi CẢ một nhóm tải lên thất bại. 400 (thiếu nguồn / chưa đồng ý theo A38) và 429 có hướng dẫn khắc phục;
    /// các trường hợp khác giữ nguyên câu của B5 (<see cref="UploadFlow.DescribeBatchFailure{T}"/>).
    /// </summary>
    public static (string Title, string Message)? DescribeUploadFailure<T>(ApiResult<T> result)
    {
        if (result.IsSuccess) return null;
        return result.StatusCode switch
        {
            400 => ("Máy chủ chưa nhận lần tải này",
                    $"{FriendlyRequestError(result.ErrorMessage)} Kiểm tra lại nguồn CV và ô xác nhận đồng ý rồi chọn lại file. Các file trong lần gửi này chưa được lưu."),
            429 => ("Tải lên quá nhanh",
                    "Máy chủ đang giới hạn số lần tải CV. Chờ khoảng một phút rồi chọn lại các file chưa được lưu."),
            _ => UploadFlow.DescribeBatchFailure(result)
        };
    }

    /// <summary>
    /// Sau khi một nhóm bị từ chối cả request (400 / 401 / 403 / 413 / 429), các nhóm còn lại mang cùng nguồn + đồng ý
    /// cũng sẽ bị từ chối: dừng, không tự gửi lại (tránh tạo bản sao khi người dùng chọn lại file).
    /// </summary>
    public static bool StopsRemainingBatches(int? statusCode) => statusCode is 400 or 401 or 403 or 413 or 429;

    public const string NotSentMessage = "Chưa gửi: lần tải đã dừng vì máy chủ từ chối nhóm file trước. Chọn lại file sau khi khắc phục.";

    /// <summary>Dòng của nhóm bị 400 / 429: thay câu kỹ thuật của API (ví dụ "(consentConfirmed = true)") bằng câu dễ hiểu.</summary>
    public static void ApplyRequestFailure(IReadOnlyList<UploadItem> batch, ApiResult<CvUploadResponse> result)
    {
        if (result.IsSuccess || result.StatusCode is not (400 or 429)) return;
        var message = result.StatusCode == 429
            ? "Chưa lưu: máy chủ đang giới hạn số lần tải. Chờ một phút rồi chọn lại file."
            : "Chưa lưu: " + FriendlyRequestError(result.ErrorMessage);
        foreach (var item in batch.Where(i => i.State == UploadItemState.Rejected)) item.Message = message;
    }

    private static string FriendlyRequestError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Yêu cầu tải lên không hợp lệ.";
        if (message.Contains("consentConfirmed", StringComparison.OrdinalIgnoreCase))
            return "Máy chủ chưa nhận được xác nhận ứng viên đã đồng ý xử lý dữ liệu.";
        if (message.Contains("sourceChannel", StringComparison.OrdinalIgnoreCase))
            return "Nguồn CV gửi lên không hợp lệ.";
        var text = TechnicalParens().Replace(message, string.Empty).Trim();
        return text.Length == 0 ? "Yêu cầu tải lên không hợp lệ." : text.EndsWith('.') ? text : text + ".";
    }

    [GeneratedRegex(@"\s*\((?:[A-Za-z]+\s*=\s*\w+|[A-Za-z.]*Exception)\)")]
    private static partial Regex TechnicalParens();

    /// <summary>
    /// A42: file trùng CV đã có → API trả Rejected + Duplicate + existingCvId (CvId = null).
    /// <see cref="UploadFlow.ApplyResult"/> (B5) chỉ biết CvId nên coi là "Bị từ chối"; hàm này nâng dòng thành "Đã có sẵn"
    /// với ĐÚNG mã CV cũ để chọn phân tích cho công việc mới. Không upload lại, không tạo mã giả.
    /// Trùng trong cùng lần tải / CV cũ đã ẩn danh hóa (không có existingCvId): giữ "Bị từ chối" kèm mã Duplicate.
    /// </summary>
    public static void ApplyDuplicateDetails(IReadOnlyList<UploadItem> batch, ApiResult<CvUploadResponse> result)
    {
        if (!result.IsSuccess || result.Data is null) return;
        var files = result.Data.Files;
        for (var i = 0; i < batch.Count; i++)
        {
            var item = batch[i];
            // Cùng cách ghép dòng như UploadFlow.ApplyResult: theo thứ tự, kiểm tra thêm tên file
            var r = i < files.Count && files[i].FileName == item.FileName
                ? files[i]
                : files.FirstOrDefault(f => f.FileName == item.FileName);
            if (r is null || r.Status != CvUploadStatuses.Rejected || r.ErrorCode != CvUploadErrorCodes.Duplicate) continue;
            if (r.ExistingCvId is not { } id || id == Guid.Empty) continue;

            var jobs = (r.ExistingJobTitles ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).ToList();
            item.State = UploadItemState.Existing;
            item.CvId = id;
            item.ErrorCode = CvUploadErrorCodes.Duplicate;
            item.Selected = true;
            item.ExistingUploadedAt = r.ExistingUploadedAt;
            item.ExistingJobTitles = jobs;
            item.HasDuplicateDetails = true;
            item.Message = DuplicateText(r.ExistingUploadedAt, jobs);
        }
    }

    /// <summary>"Đã có từ ngày 05/11/2026; đã ứng tuyển: Backend Developer (.NET), Tester (QA)." (ngày theo giờ Việt Nam).</summary>
    public static string DuplicateText(DateTime? existingUploadedAtUtc, IReadOnlyCollection<string>? jobTitles)
    {
        var since = existingUploadedAtUtc is { } d && d != default ? $"Đã có từ ngày {VnDate(d)}" : "Đã có trong hệ thống";
        var jobs = jobTitles is { Count: > 0 } ? "đã ứng tuyển: " + string.Join(", ", jobTitles) : "chưa ứng tuyển vị trí nào";
        return $"{since}; {jobs}.";
    }

    /// <summary>Ngày theo giờ Việt Nam (UTC+7, không có giờ mùa hè), không phụ thuộc múi giờ của máy chủ Web.</summary>
    public static string VnDate(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),   // API trả giờ UTC
            _ => value
        };
        return utc.AddHours(7).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Chữ hiển thị ở dòng phụ. Chế độ Ẩn danh (B36): câu trùng do API viết có thể chứa tên file của hồ sơ cũ
    /// → thay bằng câu chung. Câu trùng do giao diện tự viết (ngày + vị trí) không chứa tên nên giữ nguyên.
    /// </summary>
    public static string? DisplayMessage(UploadItem item, bool anonymous)
    {
        if (!anonymous || !item.IsDuplicate || item.HasDuplicateDetails) return item.Message;
        return item.State == UploadItemState.Existing ? "CV này đã có trong hệ thống." : "Trùng nội dung với một CV khác.";
    }

    /// <summary>Nhãn trạng thái tải lên. Trùng nhưng không dùng lại được: "Trùng hồ sơ" (giữ ngữ nghĩa Duplicate, không gọi chung là lỗi).</summary>
    public static (string Text, BadgeVariant Variant) UploadBadge(UploadItem item) =>
        item.State == UploadItemState.Rejected && item.IsDuplicate
            ? ("Trùng hồ sơ", BadgeVariant.Warning)
            : StatusText.ForUpload(item.State);

    // =====================================================================
    // Chữ đã đọc (A7 + A42)
    // =====================================================================

    /// <summary>
    /// Cập nhật dòng từ GET /api/cvs/{id}. Trả true khi không cần hỏi tiếp (đọc xong, đọc lỗi, đã ẩn danh hóa).
    /// Chỉ lấy trạng thái + chỉ số chất lượng; KHÔNG giữ ExtractedText.
    /// </summary>
    public static bool ApplyDetail(UploadItem item, CvDetailDto detail)
    {
        if (detail.CvId != item.CvId) return false;   // phản hồi không khớp dòng: bỏ qua
        if (detail.IsAnonymized) return MarkAnonymized(item);
        return ApplyExtraction(item, detail.ExtractionStatus, detail.ExtractionMethod, detail.TextQuality);
    }

    /// <summary>Cập nhật dòng từ GET /api/cvs/{id}/text (khi HR mở hộp "Chữ đã đọc"). Trả true khi đã có kết quả cuối.</summary>
    public static bool ApplyText(UploadItem item, CvTextDto text) =>
        text.CvId == item.CvId && ApplyExtraction(item, text.ExtractionStatus, text.ExtractionMethod, text.Quality);

    public static bool MarkAnonymized(UploadItem item)
    {
        item.TextCheck = TextCheck.Anonymized;
        item.ExtractionStatus = null;
        item.ExtractionMethod = null;
        item.Quality = null;
        return true;
    }

    private static bool ApplyExtraction(UploadItem item, string? status, string? method, TextQualityDto? quality)
    {
        item.ExtractionStatus = status;
        item.ExtractionMethod = method;
        if (status == ExtractionStatuses.Completed)
        {
            item.Quality = quality;
            item.TextCheck = TextCheck.Done;
            return true;
        }
        item.Quality = null;   // Pending/Failed: A42 không đánh giá, không bịa chất lượng
        if (status == ExtractionStatuses.Failed)
        {
            item.TextCheck = TextCheck.Done;
            return true;
        }
        item.TextCheck = TextCheck.Reading;
        return false;
    }

    /// <summary>Nhãn trạng thái chữ đã đọc dưới tên file. Không có nội dung CV, chỉ con số.</summary>
    public static TextStatusView TextStatus(UploadItem item)
    {
        if (item.CvId is null) return new(TextTone.None, string.Empty, null, null);
        switch (item.TextCheck)
        {
            case TextCheck.Anonymized:
                return new(TextTone.Muted, "Hồ sơ đã được ẩn danh hóa", "Không còn nội dung để xem.", null);
            case TextCheck.NotFound:
                return new(TextTone.Muted, "Không còn CV này trên máy chủ", null, null);
        }

        if (item.ExtractionStatus == ExtractionStatuses.Failed)
            return new(TextTone.Failed, "Không đọc được chữ", "AI sẽ không chấm được CV này.", "⚠ Không đọc được chữ · Xem chi tiết");

        if (item.ExtractionStatus == ExtractionStatuses.Completed)
        {
            var q = item.Quality;
            if (q is null) return new(TextTone.Muted, "Đã đọc xong", null, null);
            if (q.Level == TextQualityLevels.Empty)
                return new(TextTone.Review, "Không có chữ", "AI sẽ không có nội dung để chấm.", "⚠ Không có chữ · Xem chữ đã đọc");
            if (q.MaybeMisread)
                return new(TextTone.Review, "Có thể đọc sai", $"{CountText(q.CharCount)} ký tự", "⚠ Có thể đọc sai · Xem chữ đã đọc");
            return new(TextTone.Good, "Đọc tốt", $"{CountText(q.CharCount)} ký tự", null);
        }

        return item.TextCheck switch
        {
            TextCheck.TimedOut => new(TextTone.Muted, "Chưa đọc xong sau 2 phút", null, "Kiểm tra lại"),
            TextCheck.Unavailable => new(TextTone.Muted, "Chưa kiểm tra được chữ đã đọc", "Mất kết nối tới máy chủ.", "Thử lại"),
            _ => new(TextTone.Reading, "Đang đọc chữ…", null, null)
        };
    }

    /// <summary>Số CV có CvId cần HR xem lại chữ (có thể đọc sai, không có chữ, đọc lỗi).</summary>
    public static int ReviewCount(IEnumerable<UploadItem> items) => items.Count(i => i.CvId is not null && i.NeedsTextReview);

    /// <summary>Số CV đang được chọn để phân tích mà chưa đọc chữ xong.</summary>
    public static int ReadingSelectedCount(IEnumerable<UploadItem> items) =>
        items.Count(i => i.CanAnalyze && i.Selected && i.TextCheck is TextCheck.Reading or TextCheck.TimedOut or TextCheck.Unavailable);

    public static string MethodText(string? method) => method switch
    {
        ExtractionMethods.PdfText => "Chữ có sẵn trong PDF",
        ExtractionMethods.PdfOcr => "OCR từ PDF scan",
        ExtractionMethods.ImageOcr => "OCR từ ảnh",
        null or "" => "Chưa rõ",
        _ => method
    };

    public static string LevelText(string? level) => level switch
    {
        TextQualityLevels.Good => "Đọc tốt",
        TextQualityLevels.Low => "Có thể đọc sai",
        TextQualityLevels.Empty => "Không có chữ",
        _ => "Chưa đánh giá"
    };

    /// <summary>LetterRatio là tỷ lệ 0–1 (A42 đã cắt còn 2 chữ số) → "68%".</summary>
    public static string RatioText(decimal ratio) => $"{Math.Floor(Math.Clamp(ratio, 0m, 1m) * 100m):0}%";

    public static string CountText(int value) => value.ToString("N0", Vi);

    /// <summary>Câu báo lỗi của hộp "Chữ đã đọc" theo mã HTTP; không hiện chi tiết kỹ thuật.</summary>
    public static (string Title, string Message, bool CanRetry) TextLoadError(int? statusCode, bool timedOut) => statusCode switch
    {
        410 => ("Hồ sơ đã được ẩn danh hóa",
                "Theo chính sách lưu trữ, file gốc và nội dung CV này không còn được cung cấp. Điểm và lịch sử quyết định vẫn được giữ.", false),
        404 => ("Không tìm thấy CV", "CV này không còn trên máy chủ (có thể đã bị xóa).", false),
        >= 500 => ("Máy chủ API đang gặp lỗi", "Chưa tải được chữ đã đọc. Vui lòng thử lại sau ít phút.", true),
        null when timedOut => ("Máy chủ phản hồi quá lâu", "Chưa tải được chữ đã đọc. Kiểm tra kết nối rồi thử lại.", true),
        null => ("Không kết nối được máy chủ API", "Chưa tải được chữ đã đọc. Kiểm tra API đang chạy rồi thử lại.", true),
        _ => ("Chưa tải được chữ đã đọc", "Máy chủ từ chối yêu cầu. Vui lòng thử lại.", true)
    };

    // =====================================================================
    // Ước tính thời gian phân tích (A42)
    // =====================================================================

    /// <summary>Chỉ hỏi ước tính khi thật sự có CV được chọn và không vượt giới hạn API (0 &lt; N ≤ 500).</summary>
    public static bool ShouldEstimate(int count) => count is > 0 and <= RunTimeEstimator.MaxCount;

    /// <summary>Dòng trước nút "Phân tích hồ sơ". Không có ước tính khớp đúng số CV → chỉ hiện "N CV".</summary>
    public static string EstimateLine(int count, RunEstimateDto? estimate) =>
        count <= 0 ? "Chưa chọn CV nào"
        : estimate is null || estimate.Count != count || string.IsNullOrWhiteSpace(estimate.Text) ? $"{count} CV"
        : estimate.Text;

    /// <summary>Dòng phụ dưới ước tính: nguồn số liệu, đang tải, hoặc chưa ước tính được (không khóa phân tích).</summary>
    public static string? EstimateNote(int count, RunEstimateDto? estimate, bool loading, bool failed)
    {
        if (count <= 0) return "Tích chọn CV để xem thời gian dự kiến.";
        if (estimate is not null && estimate.Count == count)
        {
            var basis = estimate.IsDefault || estimate.BasedOnRuns <= 0 ? "Ước tính ban đầu" : $"Ước tính theo {estimate.BasedOnRuns} lượt chấm gần nhất";
            return basis + (estimate.AiSimulated ? " · AI giả lập" : string.Empty);
        }
        if (loading) return "Đang ước tính thời gian…";
        if (failed) return "Chưa ước tính được thời gian, vẫn phân tích được.";
        return null;
    }
}
