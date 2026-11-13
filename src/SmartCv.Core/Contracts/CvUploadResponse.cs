namespace SmartCv.Core.Contracts;

// Dữ liệu API POST /api/cvs/upload trả về.
// Dùng chung cho API (bạn A) và giao diện Blazor (bạn B): hai bên đọc/ghi cùng một class nên không lệch tên trường.

/// <summary>Kết quả của cả lần tải lên (một hoặc nhiều file).</summary>
public sealed class CvUploadResponse
{
    public int TotalFiles { get; init; }
    public int SuccessCount { get; init; }   // số file không bị từ chối
    public int FailedCount { get; init; }    // số file bị từ chối (A5 mới có)
    public List<CvUploadItemResult> Files { get; init; } = [];
}

/// <summary>Kết quả của từng file.</summary>
public sealed class CvUploadItemResult
{
    public string FileName { get; init; } = string.Empty;  // tên gốc, đã bỏ đường dẫn
    public long SizeBytes { get; init; }
    public string ContentType { get; init; } = string.Empty;
    public string Status { get; init; } = CvUploadStatuses.Received;
    public Guid? CvId { get; init; }          // A6: mã CV sau khi lưu vào database
    public string? ErrorCode { get; init; }   // A5: mã lỗi để giao diện hiện thông báo
    public string? ErrorMessage { get; init; }

    // A42: chỉ có khi ErrorCode = Duplicate và trùng với CV ĐÃ CÓ trong database (để giao diện mở "Xem CV hiện có").
    // Trùng nhau trong cùng lần tải, hoặc CV cũ đã ẩn danh hóa: các trường này null / rỗng.
    public Guid? ExistingCvId { get; init; }
    public string? ExistingFileName { get; init; }
    public DateTime? ExistingUploadedAt { get; init; }          // UTC
    public List<string> ExistingJobTitles { get; init; } = [];  // các vị trí CV cũ đã ứng tuyển (không trùng lặp)
}

/// <summary>Trạng thái của từng file.</summary>
public static class CvUploadStatuses
{
    public const string Received = "Received";   // A4–A5: đã nhận, chưa lưu (từ A6 không còn dùng)
    public const string Accepted = "Accepted";   // A5–A6: hợp lệ và đã lưu
    public const string Rejected = "Rejected";   // A5: bị từ chối, xem ErrorCode
}

/// <summary>Mã lỗi của từng file (A5 dùng). Giao diện (B5) dựa vào mã này để hiện thông báo tiếng Việt.</summary>
public static class CvUploadErrorCodes
{
    public const string EmptyFile = "EmptyFile";          // file 0 byte
    public const string InvalidFormat = "InvalidFormat";  // không phải PDF/PNG/JPG
    public const string TooLarge = "TooLarge";            // vượt dung lượng cho phép
    public const string Duplicate = "Duplicate";          // trùng mã hash với CV đã có
    public const string SaveFailed = "SaveFailed";        // A6: lỗi khi lưu file / database
}
