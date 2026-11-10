namespace SmartCv.Web.Models;

/// <summary>Trạng thái một file trong "Danh sách CV" ở trang Tải lên &amp; Phân tích CV.</summary>
public enum UploadItemState
{
    Invalid,     // B5 "Lỗi file": giao diện chặn trước khi gửi (sai/không có đuôi, rỗng, quá dung lượng, trùng trong danh sách, quá số lượng)
    Uploading,   // đang gửi lên API
    Uploaded,    // API trả Accepted + CvId: đã lưu (A6)
    Existing,    // trùng CV đã có và API trả lại CvId cũ (chỉ khi API hỗ trợ) → vẫn phân tích được
    Rejected     // B5 "Bị từ chối": API từ chối nội dung, lỗi kết nối/máy chủ, hoặc API không trả CvId
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
}
