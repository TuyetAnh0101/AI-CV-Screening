namespace SmartCv.Core.Contracts;

// ======================= A24: API ứng viên =======================

/// <summary>Body của POST /api/candidates và PUT /api/candidates/{id}.</summary>
public sealed class CandidateRequest
{
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    /// <summary>true: HR đã xem gợi ý trùng và vẫn muốn tạo ứng viên MỚI (ví dụ hai người dùng chung email gia đình).</summary>
    public bool AllowDuplicate { get; set; }
}

public sealed class CandidateDto
{
    public int CandidateId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? Email { get; init; }
    public string? PhoneNumber { get; init; }
    public DateTime CreatedAt { get; init; }       // UTC
}

/// <summary>Một dòng GET /api/candidates.</summary>
public sealed class CandidateListItemDto
{
    public int CandidateId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? Email { get; init; }
    public string? PhoneNumber { get; init; }
    public DateTime CreatedAt { get; init; }       // UTC
    public int CvCount { get; init; }
    public int ApplicationCount { get; init; }
    public DateTime? LastUploadedAt { get; init; } // UTC, CV mới nhất
}

public sealed class CandidatePageDto
{
    public List<CandidateListItemDto> Items { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages { get; init; }
}

/// <summary>Gợi ý "có thể là cùng người" (khớp email hoặc điện thoại).</summary>
public sealed class DuplicateMatchDto
{
    public int CandidateId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? Email { get; init; }
    public string? PhoneNumber { get; init; }
    public List<string> MatchedOn { get; init; } = [];   // "Email", "Phone"
    public int CvCount { get; init; }
}

/// <summary>GET /api/candidates/{id}: toàn bộ lịch sử ứng tuyển của một người ở một chỗ.</summary>
public sealed class CandidateDetailDto
{
    public int CandidateId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? Email { get; init; }
    public string? PhoneNumber { get; init; }
    public DateTime CreatedAt { get; init; }       // UTC
    public List<CandidateCvDto> Cvs { get; init; } = [];
    public List<CandidateApplicationDto> Applications { get; init; } = [];
    /// <summary>Ứng viên KHÁC có cùng email / điện thoại → B24 hiện hộp thoại gộp.</summary>
    public List<DuplicateMatchDto> PossibleDuplicates { get; init; } = [];
    public List<DataRequestDto> DataRequests { get; init; } = [];
}

public sealed class CandidateCvDto
{
    public Guid CvId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public DateTime UploadedAt { get; init; }      // UTC
    public string ExtractionStatus { get; init; } = string.Empty;
    public string? SourceChannel { get; init; }
    public string SourceChannelName { get; init; } = string.Empty;
    public DateTime? ConsentConfirmedAt { get; init; }
    public DateTime? RetainUntil { get; init; }
    public DateTime? AnonymizedAt { get; init; }
    public bool IsAnonymized => AnonymizedAt is not null;
}

public sealed class CandidateApplicationDto
{
    public int ApplicationId { get; init; }
    public Guid CvId { get; init; }
    public int JobId { get; init; }
    public string JobTitle { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public string? RejectReason { get; init; }
    public DateTime AppliedAt { get; init; }       // UTC
    public DateTime? StageUpdatedAt { get; init; }
    public decimal? LatestFinalScore { get; init; }
    public string? LatestFitLabel { get; init; }
    public DateTime? LatestEvaluatedAt { get; init; }
    /// <summary>Dòng thời gian quyết định, CŨ → MỚI (ai, lúc nào, lý do).</summary>
    public List<ApplicationEventDto> Events { get; init; } = [];
}

/// <summary>
/// Body của PUT /api/cvs/{id}/candidate. Hai cách:
///   1) gắn vào ứng viên CÓ SẴN: gửi CandidateId;
///   2) tạo ứng viên MỚI: gửi FullName + Email / PhoneNumber. Nếu trùng email / điện thoại với người đã có,
///      API KHÔNG tự tạo mà trả Outcome = "NeedsDecision" kèm danh sách gợi ý; HR chọn một người (gửi lại CandidateId)
///      hoặc gửi lại với ConfirmNew = true để vẫn tạo người mới.
/// </summary>
public sealed class AttachCandidateRequest
{
    public int? CandidateId { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public bool ConfirmNew { get; set; }
}

public static class AttachOutcomes
{
    public const string Attached = "Attached";
    public const string NeedsDecision = "NeedsDecision";
}

public sealed class AttachCandidateResponse
{
    public string Outcome { get; init; } = AttachOutcomes.Attached;   // Attached | NeedsDecision
    public Guid CvId { get; init; }
    public CandidateDto? Candidate { get; init; }                      // khi Attached
    public bool CandidateCreated { get; init; }
    /// <summary>Ứng viên cũ của CV bị xóa vì không còn CV nào (CV gắn nhầm người rồi sửa lại).</summary>
    public bool PreviousCandidateRemoved { get; init; }
    public List<DuplicateMatchDto> Matches { get; init; } = [];        // khi NeedsDecision
}

/// <summary>GET /api/cvs/{id}/candidate-suggestion: thông tin đọc từ CV để điền sẵn ô nhập (HR sửa rồi lưu).</summary>
public sealed class CandidateSuggestionDto
{
    public Guid CvId { get; init; }
    public int? CurrentCandidateId { get; init; }
    public string? FullName { get; init; }
    public string? Email { get; init; }
    public string? PhoneNumber { get; init; }
    public List<DuplicateMatchDto> Matches { get; init; } = [];
}

/// <summary>Body của POST /api/candidates/{id}/merge: chuyển mọi CV của {id} sang TargetCandidateId rồi xóa {id}.</summary>
public sealed class MergeCandidateRequest
{
    public int TargetCandidateId { get; set; }
    public string? Actor { get; set; }
}

public sealed class MergeCandidateResultDto
{
    public int TargetCandidateId { get; init; }
    public int RemovedCandidateId { get; init; }
    public int CvsMoved { get; init; }
}
