namespace SmartCv.Core.Contracts;

// A8: gắn CV với job (bảng Applications). Dùng chung cho API (bạn A) và giao diện B4 (bạn B).

/// <summary>Dữ liệu gửi lên POST /api/applications: gắn nhiều CV vào MỘT job.</summary>
public sealed class CreateApplicationsRequest
{
    public int JobId { get; init; }
    public List<Guid> CvIds { get; init; } = [];
}

/// <summary>Kết quả POST /api/applications.</summary>
public sealed class CreateApplicationsResponse
{
    public int JobId { get; init; }
    public string JobTitle { get; init; } = string.Empty;
    public int CreatedCount { get; init; }    // gắn mới
    public int ExistingCount { get; init; }   // đã gắn từ trước, giữ nguyên
    public int FailedCount { get; init; }     // không gắn được (CV không tồn tại)
    public List<ApplicationItemResult> Items { get; init; } = [];
}

/// <summary>Kết quả của từng CV.</summary>
public sealed class ApplicationItemResult
{
    public Guid CvId { get; init; }
    public string? FileName { get; init; }
    public int? ApplicationId { get; init; }
    public string Status { get; init; } = string.Empty;      // xem ApplicationItemStatuses
    public string? ExtractionStatus { get; init; }           // Pending | Completed | Failed
    public string? Message { get; init; }                    // ghi chú tiếng Việt (nếu có)
}

public static class ApplicationItemStatuses
{
    public const string Created = "Created";
    public const string AlreadyExists = "AlreadyExists";
    public const string CvNotFound = "CvNotFound";
}

/// <summary>Một hồ sơ (CV + job): GET /api/applications?jobId=...</summary>
public sealed class ApplicationDto
{
    public int ApplicationId { get; init; }
    public int JobId { get; init; }
    public string JobTitle { get; init; } = string.Empty;
    public Guid CvId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string? CandidateName { get; init; }
    public string ExtractionStatus { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }   // giờ UTC
}
