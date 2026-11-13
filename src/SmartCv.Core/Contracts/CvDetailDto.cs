namespace SmartCv.Core.Contracts;

/// <summary>Thông tin một CV: GET /api/cvs/{id}. Dùng cho B9 (chi tiết CV) và B10 (trạng thái đọc file).</summary>
public sealed class CvDetailDto
{
    public Guid CvId { get; init; }
    public int? CandidateId { get; init; }
    public string? CandidateName { get; init; }
    public string OriginalFileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public DateTime UploadedAt { get; init; }            // giờ UTC
    public bool FileAvailable { get; init; }             // file gốc còn trên máy chủ không
    public string? SourceChannel { get; init; }
    public string SourceChannelName { get; init; } = string.Empty;
    public DateTime? ConsentConfirmedAt { get; init; }
    public DateTime? RetainUntil { get; init; }
    public DateTime? AnonymizedAt { get; init; }
    public bool IsAnonymized => AnonymizedAt is not null;

    // A7: kết quả đọc chữ
    public string ExtractionStatus { get; init; } = string.Empty;   // Pending | Completed | Failed
    public string? ExtractionMethod { get; init; }                  // PdfText | PdfOcr | ImageOcr
    public string? ExtractionError { get; init; }
    public string? ExtractedText { get; init; }

    // A42: chất lượng chữ đọc được; null khi chưa đọc xong (Pending), đọc lỗi (Failed) hoặc CV đã ẩn danh hóa
    public TextQualityDto? TextQuality { get; init; }

    // A8: các job CV này đã ứng tuyển
    public List<CvApplicationDto> Applications { get; init; } = [];
}

public sealed class CvApplicationDto
{
    public int ApplicationId { get; init; }
    public int JobId { get; init; }
    public string JobTitle { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}
