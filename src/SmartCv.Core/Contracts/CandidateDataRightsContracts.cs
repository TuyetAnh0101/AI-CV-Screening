namespace SmartCv.Core.Contracts;

// ======================= A43: quyền của ứng viên với dữ liệu =======================

public static class DataRequestTypes
{
    public const string Export = "Export";
    public const string Erase = "Erase";

    public static string DisplayName(string? type) => type switch
    {
        Export => "Xem / xuất dữ liệu",
        Erase => "Xóa dữ liệu",
        _ => type ?? string.Empty
    };
}

/// <summary>Body của POST /api/candidates/{id}/erase.</summary>
public sealed class EraseCandidateRequest
{
    /// <summary>Bắt buộc true: chống bấm nhầm (giao diện có xác nhận 2 bước).</summary>
    public bool Confirm { get; set; }
    public string? HandledBy { get; set; }
    /// <summary>Ứng viên yêu cầu qua kênh nào, ngày nào... (không ghi thông tin cá nhân vào đây).</summary>
    public string? Note { get; set; }
}

public sealed class EraseCandidateResultDto
{
    public long RequestId { get; init; }
    public int CandidateId { get; init; }
    public int CvsAnonymized { get; init; }
    public int CvsAlreadyAnonymized { get; init; }
    public int ApplicationsWithdrawn { get; init; }
    public bool CandidateDeleted { get; init; }
    public DateTime HandledAt { get; init; }      // UTC
}

/// <summary>Một dòng nhật ký yêu cầu của ứng viên. KHÔNG chứa tên / email (sau khi xóa vẫn giữ được làm bằng chứng).</summary>
public sealed class DataRequestDto
{
    public long RequestId { get; init; }
    public int CandidateId { get; init; }
    public string RequestType { get; init; } = string.Empty;
    public string RequestTypeName { get; init; } = string.Empty;
    public string HandledBy { get; init; } = string.Empty;
    public string? Note { get; init; }
    public string? ResultSummary { get; init; }
    public DateTime CreatedAt { get; init; }      // UTC
}

/// <summary>GET /api/candidates/{id}/data-export: mọi dữ liệu đang lưu về ứng viên.</summary>
public sealed class CandidateDataExportDto
{
    public DateTime ExportedAt { get; init; }     // UTC
    public string ExportedBy { get; init; } = string.Empty;
    public string Notice { get; init; } = string.Empty;
    public CandidateDto Candidate { get; init; } = new();
    public List<ExportCvDto> Cvs { get; init; } = [];
    public List<DataRequestDto> PreviousRequests { get; init; } = [];
}

public sealed class ExportCvDto
{
    public Guid CvId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public DateTime UploadedAt { get; init; }
    public string? SourceChannel { get; init; }
    public DateTime? ConsentConfirmedAt { get; init; }
    public DateTime? RetainUntil { get; init; }
    public DateTime? AnonymizedAt { get; init; }
    public string? ExtractedText { get; init; }
    public List<ExportApplicationDto> Applications { get; init; } = [];
}

public sealed class ExportApplicationDto
{
    public int ApplicationId { get; init; }
    public string JobTitle { get; init; } = string.Empty;
    public DateTime AppliedAt { get; init; }
    public string Stage { get; init; } = string.Empty;
    public string? RejectReason { get; init; }
    public List<ExportEvaluationDto> Evaluations { get; init; } = [];
    public List<ApplicationEventDto> Events { get; init; } = [];
    public List<ExportHrLabelDto> HrLabels { get; init; } = [];
}

public sealed class ExportEvaluationDto
{
    public DateTime CreatedAt { get; init; }
    public string RunType { get; init; } = string.Empty;
    public decimal? KeywordScore { get; init; }
    public decimal? AiScore { get; init; }
    public decimal? FinalScore { get; init; }
    public string? FitLabel { get; init; }
    public decimal? YearsExperience { get; init; }
    public string? Reasoning { get; init; }
    public List<ExportSkillDto> Skills { get; init; } = [];
}

public sealed class ExportSkillDto
{
    public string SkillCode { get; init; } = string.Empty;
    public bool IsMatched { get; init; }
    public string? Evidence { get; init; }
}

public sealed class ExportHrLabelDto
{
    public string EvaluationSetName { get; init; } = string.Empty;
    public bool IsPass { get; init; }
    public string? Note { get; init; }
    public string LabeledBy { get; init; } = string.Empty;
    public DateTime LabeledAt { get; init; }
}
