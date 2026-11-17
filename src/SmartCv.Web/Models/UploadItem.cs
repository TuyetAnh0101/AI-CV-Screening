using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;

namespace SmartCv.Web.Models;

/// <summary>Trạng thái một file trong "Danh sách CV" ở trang Tải lên &amp; Phân tích CV.</summary>
public enum UploadItemState
{
    Invalid,     // B5 "Lỗi file": giao diện chặn trước khi gửi (sai/không có đuôi, rỗng, quá dung lượng, trùng trong danh sách, quá số lượng)
    Uploading,   // đang gửi lên API
    Uploaded,    // API trả Accepted + CvId: đã lưu (A6)
    Existing,    // trùng CV đã có và API trả lại mã CV cũ (A42 existingCvId) → vẫn phân tích được cho công việc mới
    Rejected     // B5 "Bị từ chối": API từ chối nội dung, lỗi kết nối/máy chủ, hoặc API không trả mã CV
}

/// <summary>B38: tiến trình kiểm tra chữ đã đọc (A7 đọc nền, A42 đánh giá chất lượng) của một dòng đã có CvId.</summary>
public enum TextCheck
{
    None,          // chưa theo dõi (dòng chưa có CvId)
    Reading,       // đang hỏi GET /api/cvs/{id} mỗi 2 giây
    Done,          // API đã trả Completed hoặc Failed
    TimedOut,      // quá 2 phút vẫn Pending: dừng hỏi, HR mở "Xem chữ đã đọc" để kiểm tra lại
    Unavailable,   // không hỏi được API (mất kết nối, máy chủ lỗi) tới hết thời gian chờ
    Anonymized,    // CV đã được ẩn danh hóa (A39): không còn chữ để xem
    NotFound       // CV không còn trên máy chủ
}

/// <summary>Một dòng trong "Danh sách CV".</summary>
public sealed class UploadItem
{
    public Guid LocalId { get; } = Guid.NewGuid();     // mã tạm trên giao diện (không phải CvId)
    public required string FileName { get; init; }
    public long SizeBytes { get; init; }
    public UploadItemState State { get; set; }
    public string? Message { get; set; }               // dòng phụ dưới tên file: lý do lỗi, ghi chú
    public string? ErrorCode { get; set; }             // B5: CvUploadErrorCodes do API trả (null nếu lỗi ở giao diện / kết nối)
    public Guid? CvId { get; set; }

    /// <summary>B5: dòng lỗi (Lỗi file hoặc Bị từ chối): không phân tích được, ô tích bị khóa, được "Xóa N file lỗi".</summary>
    public bool IsError => State is UploadItemState.Invalid or UploadItemState.Rejected;
    public bool Selected { get; set; } = true;         // ô tích: có đưa vào lượt phân tích (B6) không

    /// <summary>Chỉ CV đã có CvId thật trên máy chủ mới được chọn để phân tích.</summary>
    public bool CanAnalyze => CvId is not null && State is UploadItemState.Uploaded or UploadItemState.Existing;

    public string Extension => Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant();

    // ---------- B38 (A42): CV trùng với CV đã có ----------
    /// <summary>Ngày (UTC) CV cũ được tải lên; null khi API không trả (trùng trong cùng lần tải, CV cũ đã ẩn danh hóa).</summary>
    public DateTime? ExistingUploadedAt { get; set; }
    /// <summary>Các vị trí CV cũ đã ứng tuyển. Không giữ tên file của CV cũ (không hiển thị, giảm dữ liệu cá nhân trên giao diện).</summary>
    public List<string> ExistingJobTitles { get; set; } = [];
    /// <summary>Đã nhận đủ thông tin CV cũ từ A42 (existingCvId) nên Message do giao diện tự viết, không chứa tên file.</summary>
    public bool HasDuplicateDetails { get; set; }
    public bool IsDuplicate => ErrorCode == CvUploadErrorCodes.Duplicate;

    // ---------- B38 (A42): chữ đã đọc ----------
    public TextCheck TextCheck { get; set; }
    public string? ExtractionStatus { get; set; }      // Pending | Completed | Failed (null: chưa hỏi)
    public string? ExtractionMethod { get; set; }      // PdfText | PdfOcr | ImageOcr
    /// <summary>Chỉ số chất lượng (không có nội dung CV). Chữ đã đọc KHÔNG được giữ trong dòng: chỉ tải khi mở hộp "Chữ đã đọc".</summary>
    public TextQualityDto? Quality { get; set; }

    /// <summary>A42: đã đọc xong nhưng chất lượng Low/Empty → cảnh báo vàng "Có thể đọc sai".</summary>
    public bool MaybeMisread => ExtractionStatus == ExtractionStatuses.Completed && Quality?.MaybeMisread == true;
    public bool ExtractionFailed => ExtractionStatus == ExtractionStatuses.Failed;
    /// <summary>HR nên xem lại trước khi chấm: có thể đọc sai, không có chữ, hoặc đọc lỗi.</summary>
    public bool NeedsTextReview => MaybeMisread || ExtractionFailed;
}
