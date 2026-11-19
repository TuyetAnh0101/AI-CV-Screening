namespace SmartCv.Core.Contracts;

// =====================================================================================
// SCApp v2: thành viên bộ CV đánh giá, tách khỏi nhãn HR.
//   Thành viên = hồ sơ ứng tuyển (ApplicationId = một CV ứng tuyển một Job), KHÔNG phải CvId.
//   Nhãn HR (Đạt/Không đạt) là lựa chọn riêng; thành viên chưa có nhãn = "Chưa đánh giá".
// =====================================================================================

/// <summary>Một thành viên của bộ, kèm nhãn HR hiện tại (nếu có) và tình trạng sẵn sàng.</summary>
public sealed class EvaluationSetMemberDto
{
    public int ApplicationId { get; init; }
    public Guid CvId { get; init; }
    public string? CandidateName { get; init; }
    public string FileName { get; init; } = string.Empty;
    public int JobId { get; init; }
    public string JobTitle { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public string ExtractionStatus { get; init; } = string.Empty;
    public bool HasExtractedText { get; init; }
    public bool IsAnonymized { get; init; }
    /// <summary>true: CV đọc xong, có chữ và chưa ẩn danh — chạy PromptTest được.</summary>
    public bool IsReady { get; init; }
    public string? NotReadyReason { get; init; }
    /// <summary>null = Chưa đánh giá; true = Đạt; false = Không đạt.</summary>
    public bool? IsPass { get; init; }
    public string? Note { get; init; }
    public string? LabeledBy { get; init; }
    public DateTime? LabeledAt { get; init; }   // UTC
    public string AddedBy { get; init; } = string.Empty;
    public DateTime AddedAt { get; init; }      // UTC
}

/// <summary>Số liệu kiểm tra bộ trước khi chạy (bước 4).</summary>
public sealed class EvaluationSetReadinessDto
{
    public int MemberCount { get; init; }
    public int LabeledCount { get; init; }
    public int PassCount { get; init; }
    public int FailCount { get; init; }
    public int UnlabeledCount { get; init; }
    /// <summary>Thành viên ĐÃ gán nhãn nhưng CV chưa sẵn sàng: chặn chạy PromptTest.</summary>
    public int LabeledNotReadyCount { get; init; }
    /// <summary>Mọi thành viên chưa sẵn sàng (gồm cả chưa gán nhãn).</summary>
    public int NotReadyCount { get; init; }
    public bool CanRun { get; init; }
    /// <summary>Lý do chưa chạy được, hoặc cảnh báo khi vẫn chạy được (ví dụ còn hồ sơ chưa gán nhãn).</summary>
    public List<string> Blockers { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed class AddEvaluationSetMembersRequest
{
    public List<int> ApplicationIds { get; set; } = [];
    public string AddedBy { get; set; } = string.Empty;   // NVARCHAR(100)
}

public sealed class SkippedMemberDto
{
    public int ApplicationId { get; init; }
    /// <summary>AlreadyMember | NotFound | Anonymized</summary>
    public string Reason { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class AddEvaluationSetMembersResult
{
    public int Added { get; init; }
    public List<int> AddedApplicationIds { get; init; } = [];
    public List<SkippedMemberDto> Skipped { get; init; } = [];
    public int MemberCount { get; init; }
    public int Revision { get; init; }
    public List<string> Warnings { get; init; } = [];
}

public sealed class RemoveEvaluationSetMemberResult
{
    public int ApplicationId { get; init; }
    public bool RemovedLabel { get; init; }
    public int MemberCount { get; init; }
    public int Revision { get; init; }
    public List<int> RecalculatedRunIds { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public static class SkippedMemberReasons
{
    public const string AlreadyMember = "AlreadyMember";
    public const string NotFound = "NotFound";
    public const string Anonymized = "Anonymized";
}
