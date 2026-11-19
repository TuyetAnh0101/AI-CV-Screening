namespace SmartCv.Core.Contracts;

// =====================================================================================
// SCApp v2: cờ "Cần kiểm tra lại" do HR gắn thủ công (khác cảnh báo tự động ReviewFlagDto).
//   GET  /api/applications/{id}/flags                 cờ hiện tại + lịch sử
//   POST /api/applications/{id}/flags                 đánh dấu cần kiểm tra
//   POST /api/applications/flags/{flagId}/resolve     đánh dấu đã xử lý (ExpectedVersion bắt buộc)
//   POST /api/applications/flags/{flagId}/reopen      mở lại (ExpectedVersion bắt buộc)
// Không thao tác nào ở đây đổi giai đoạn tuyển dụng.
// =====================================================================================

public sealed class ManualFlagDto
{
    public int FlagId { get; init; }
    public int ApplicationId { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string ReasonName { get; init; } = string.Empty;
    public string? Note { get; init; }
    public string Status { get; init; } = string.Empty;       // Open | Resolved
    public string CreatedBy { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }                  // UTC
    public string? ResolvedBy { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string? ResolutionNote { get; init; }
    public DateTime UpdatedAt { get; init; }
    public Guid Version { get; init; }
}

public sealed class ManualFlagEventDto
{
    public long EventId { get; init; }
    public int FlagId { get; init; }
    public string Action { get; init; } = string.Empty;       // Opened | Resolved | Reopened
    public string ActionName { get; init; } = string.Empty;
    public string Actor { get; init; } = string.Empty;
    public string? Note { get; init; }
    public DateTime CreatedAt { get; init; }                  // UTC
}

public sealed class ApplicationFlagsResponse
{
    public int ApplicationId { get; init; }
    /// <summary>Cờ đang mở, hoặc null.</summary>
    public ManualFlagDto? Current { get; init; }
    /// <summary>Mọi cờ của hồ sơ, mới nhất trước (kể cả đã xử lý).</summary>
    public List<ManualFlagDto> Flags { get; init; } = [];
    /// <summary>Lịch sử mọi cờ, mới nhất trước.</summary>
    public List<ManualFlagEventDto> Events { get; init; } = [];
}

public sealed class CreateManualFlagRequest
{
    public string Reason { get; set; } = string.Empty;
    public string? Note { get; set; }      // tối đa 500; bắt buộc khi Reason = Other
    public string Actor { get; set; } = string.Empty;
}

public sealed class ChangeManualFlagRequest
{
    public string Actor { get; set; } = string.Empty;
    public string? Note { get; set; }      // tối đa 500
    public Guid? ExpectedVersion { get; set; }
}

public sealed class ManualFlagReasonDto
{
    public string Code { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool NoteRequired { get; init; }
}
