namespace SmartCv.Core.Entities;

/// <summary>Bảng Cvs - File và nội dung CV. CvId là GUID, cũng là tên file lưu trên ổ đĩa.</summary>
public class Cv
{
    public Guid CvId { get; set; }
    public int? CandidateId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredPath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string FileHash { get; set; } = string.Empty;       // SHA-256, 64 ký tự hex
    public string? ExtractedText { get; set; }
    public string? ExtractionMethod { get; set; }              // PdfText | PdfOcr | ImageOcr
    public string ExtractionStatus { get; set; } = "Pending";  // Pending | Completed | Failed
    public string? ExtractionError { get; set; }
    public DateTime UploadedAt { get; set; }
    public string? SourceChannel { get; set; }
    public DateTime? ConsentConfirmedAt { get; set; }
    public DateTime? RetainUntil { get; set; }
    public DateTime? AnonymizedAt { get; set; }

    public Candidate? Candidate { get; set; }
    public ICollection<Application> Applications { get; set; } = new List<Application>();
}
