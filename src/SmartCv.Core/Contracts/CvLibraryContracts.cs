namespace SmartCv.Core.Contracts;

/// <summary>A23: một trang kết quả GET /api/cvs (kho CV).</summary>
public sealed class CvPageDto
{
    public List<CvListItemDto> Items { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages { get; init; }
    /// <summary>Thời gian truy vấn database (ms) – để nhóm theo dõi khi kho CV lớn lên.</summary>
    public long ElapsedMs { get; init; }
}

/// <summary>
/// A23: một dòng trong kho CV. CV đã ẩn danh hóa (A39) không còn tên file gốc, ứng viên, email:
/// API trả tên file thay thế và để trống các trường cá nhân.
/// </summary>
public sealed class CvListItemDto
{
    public Guid CvId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public DateTime UploadedAt { get; init; }              // UTC
    public string ExtractionStatus { get; init; } = string.Empty;
    public string? SourceChannel { get; init; }
    public string SourceChannelName { get; init; } = string.Empty;
    public DateTime? RetainUntil { get; init; }            // UTC, hạn lưu (null: còn đang xét)
    public DateTime? AnonymizedAt { get; init; }           // UTC
    public bool IsAnonymized => AnonymizedAt is not null;
    public int? CandidateId { get; init; }
    public string? CandidateName { get; init; }
    public string? CandidateEmail { get; init; }

    /// <summary>Trạng thái mới nhất của CV ở TỪNG job đã ứng tuyển + điểm sàng lọc gần nhất.</summary>
    public List<CvJobStatusDto> Applications { get; init; } = [];

    /// <summary>Điểm của lần chấm sàng lọc gần nhất trong mọi job (null: chưa có lần chấm sàng lọc xong).</summary>
    public decimal? LatestScore => Applications
        .Where(a => a.LatestEvaluatedAt is not null)
        .OrderByDescending(a => a.LatestEvaluatedAt)
        .Select(a => a.LatestFinalScore)
        .FirstOrDefault();
}

/// <summary>A23: trạng thái của CV ở một job (giai đoạn HR hiện tại + điểm sàng lọc gần nhất).</summary>
public sealed class CvJobStatusDto
{
    public int ApplicationId { get; init; }
    public int JobId { get; init; }
    public string JobTitle { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public string? RejectReason { get; init; }
    public DateTime? StageUpdatedAt { get; init; }         // UTC
    /// <summary>Lượt SÀNG LỌC (Screening) chấm xong gần nhất; lượt đánh giá prompt không tính.</summary>
    public decimal? LatestFinalScore { get; init; }
    public string? LatestFitLabel { get; init; }
    /// <summary>UTC, thời điểm tạo bản chấm đó (Evaluations.CreatedAt).</summary>
    public DateTime? LatestEvaluatedAt { get; init; }
}
