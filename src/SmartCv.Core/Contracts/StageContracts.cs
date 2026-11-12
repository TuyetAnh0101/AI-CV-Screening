namespace SmartCv.Core.Contracts;

// A35: đổi giai đoạn hồ sơ và lịch sử quyết định. Dùng chung API và giao diện (B34, B35).
//   PATCH /api/applications/{id}/stage    đổi một hồ sơ
//   POST  /api/applications/stage         đổi nhiều hồ sơ (một giao dịch)
//   GET   /api/applications/{id}/events   lịch sử, mới nhất trước
//   GET   /api/applications/stage-options danh sách giai đoạn và lý do loại (chữ hiển thị)

public sealed class ChangeStageRequest
{
    public string ToStage { get; set; } = string.Empty;      // New | Reviewing | Shortlisted | Interview | Rejected | OnHold
    public string? RejectReason { get; set; }                 // bắt buộc khi ToStage = Rejected
    public string? Note { get; set; }                         // tối đa 500 ký tự
    public string Actor { get; set; } = string.Empty;         // người thao tác (trước M6: tên HR tự nhập)
    /// <summary>Tùy chọn: giai đoạn giao diện đang thấy. Khác giai đoạn thật (người khác vừa đổi) → 409.</summary>
    public string? ExpectedStage { get; set; }
    /// <summary>Optional version from ranking/detail. Detects reason changes and stage A→B→A.</summary>
    public Guid? ExpectedStageVersion { get; set; }
}

public sealed class BulkChangeStageRequest
{
    public List<int> ApplicationIds { get; set; } = [];       // tối đa 200
    public string ToStage { get; set; } = string.Empty;
    public string? RejectReason { get; set; }
    public string? Note { get; set; }
    public string Actor { get; set; } = string.Empty;
    /// <summary>Optional version per requested application; keys must belong to ApplicationIds.</summary>
    public Dictionary<int, Guid>? ExpectedStageVersions { get; set; }
}

/// <summary>Kết quả đổi một hồ sơ.</summary>
public sealed class StageChangeResultDto
{
    public int ApplicationId { get; init; }
    /// <summary>Changed | Unchanged | NotFound; a bulk 409 also returns Conflict | RolledBack.</summary>
    public string Outcome { get; init; } = string.Empty;
    public string? FromStage { get; init; }
    public string? Stage { get; init; }                       // giai đoạn SAU khi xử lý
    public string? RejectReason { get; init; }
    public DateTime? StageUpdatedAt { get; init; }            // UTC
    public Guid? StageVersion { get; init; }
    public long? EventId { get; init; }                       // dòng lịch sử vừa ghi (Changed)
    public string? Message { get; init; }
}

public sealed class BulkStageChangeResponse
{
    public int Total { get; init; }
    public int Changed { get; init; }
    public int Unchanged { get; init; }
    public int NotFound { get; init; }
    public List<StageChangeResultDto> Results { get; init; } = [];
}

/// <summary>Một dòng lịch sử (bảng ApplicationEvents).</summary>
public sealed class ApplicationEventDto
{
    public long EventId { get; init; }
    public int ApplicationId { get; init; }
    public string FromStage { get; init; } = string.Empty;
    public string ToStage { get; init; } = string.Empty;
    public string? RejectReason { get; init; }
    public string? Note { get; init; }
    public string Actor { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }                  // UTC; giao diện đổi sang giờ Việt Nam
}

public sealed class StageOptionDto
{
    public string Code { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

public sealed class StageOptionsResponse
{
    public List<StageOptionDto> Stages { get; init; } = [];
    public List<StageOptionDto> RejectReasons { get; init; } = [];
}
