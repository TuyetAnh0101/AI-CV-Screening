namespace SmartCv.Core.Contracts;

// Hợp đồng dữ liệu cho trang Prompt & độ chính xác (B12–B16).
// Bạn A dùng đúng các lớp này khi làm API A14–A17; bạn B dùng ngay (với FakePromptApi) để dựng giao diện song song.
//
// Đường dẫn API thống nhất:
//   GET  /api/prompts                          danh sách phiên bản (mới nhất trước)              A14 · B12
//   POST /api/prompts                          tạo phiên bản mới (không sửa bản cũ)               A14 · B12
//   POST /api/prompts/{id}/activate            kích hoạt (chỉ 1 bản IsActive)                     A14 · B13
//   GET  /api/evaluation-sets                  các bộ CV đã có nhãn HR                            A15 · B14
//   POST /api/prompt-tests                     chạy prompt trên bộ đánh giá (RunType=PromptTest)  A15 · B14
//   GET  /api/prompt-tests?evaluationSetId=1   các lần chạy đánh giá của một bộ                   A16/A17 · B15
//   GET  /api/prompt-tests/{runId}             tiến độ + từng CV (đúng/sai so với HR)             A16 · B14, B16
//
// Bổ sung khi làm A14–A17 (chỉ THÊM lớp mới, không đổi lớp cũ → giao diện B12–B16 không phải sửa):
//   GET    /api/prompts/{id}                                 một phiên bản                              A14
//   GET    /api/prompts/active                               bản đang dùng                              A14
//   POST   /api/evaluation-sets                              tạo bộ CV đánh giá                         A15
//   GET    /api/evaluation-sets/{id}                         bộ CV + toàn bộ nhãn HR                     A15
//   GET    /api/evaluation-sets/{id}/applications            hồ sơ có thể gán nhãn (đã / chưa gán)      A15
//   PUT    /api/evaluation-sets/{id}/labels                  gán / sửa nhãn (JSON)                      A15
//   POST   /api/evaluation-sets/{id}/labels/import           nhập nhãn từ file CSV (Excel)              A15
//   GET    /api/evaluation-sets/{id}/labels/template         tải file CSV mẫu để HR điền                A15
//   DELETE /api/evaluation-sets/{id}/labels/{applicationId}  bỏ nhãn một hồ sơ                          A15
//   GET    /api/prompt-tests/compare?baseRunId=1&candidateRunId=2                                       A17
//   GET    /api/prompts/compare?baseVersionId=1&candidateVersionId=2&evaluationSetId=1               A17

/// <summary>Một phiên bản system prompt (bảng PromptVersions) kèm kết quả đánh giá gần nhất.</summary>
public sealed class PromptVersionDto
{
    public int PromptVersionId { get; init; }
    public string VersionName { get; init; } = string.Empty;
    public string SystemPrompt { get; init; } = string.Empty;
    public string? ChangeNote { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }            // UTC
    public bool HasRuns { get; init; }                  // đã dùng để chấm → không được sửa, chỉ lưu thành bản mới
    public decimal? LatestAccuracyPercent { get; init; } // lần PromptTest hoàn thành gần nhất (null: chưa đánh giá)
}

public sealed class CreatePromptVersionRequest
{
    public string VersionName { get; set; } = string.Empty;   // NVARCHAR(100), không trùng
    public string SystemPrompt { get; set; } = string.Empty;
    public string? ChangeNote { get; set; }                    // NVARCHAR(500)
}

/// <summary>Bộ CV đánh giá (EvaluationSets) và số nhãn HR.</summary>
public sealed class EvaluationSetDto
{
    public int EvaluationSetId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public int LabelCount { get; init; }   // số CV đã gán nhãn
    public int PassCount { get; init; }    // số nhãn Đạt
}

public sealed class StartPromptTestRequest
{
    public int PromptVersionId { get; set; }
    public int EvaluationSetId { get; set; }
}

/// <summary>Một lần chạy đánh giá prompt (EvaluationRuns với RunType = PromptTest).</summary>
public class PromptTestSummaryDto
{
    public int RunId { get; init; }
    public int PromptVersionId { get; init; }
    public string PromptVersionName { get; init; } = string.Empty;
    public int EvaluationSetId { get; init; }
    public string EvaluationSetName { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;   // Queued | Running | Completed | Failed
    public int TotalItems { get; init; }
    public int ProcessedItems { get; init; }
    public int FailedItems { get; init; }

    // A16: chỉ có khi Status = Completed
    public decimal? AccuracyPercent { get; init; }   // số CV AI giống HR / tổng × 100
    public int CorrectCount { get; init; }
    public int MissedCount { get; init; }            // bỏ sót: HR Đạt, AI Không đạt
    public int WrongPickCount { get; init; }         // chọn nhầm: HR Không đạt, AI Đạt

    public DateTime CreatedAt { get; init; }         // UTC
    public DateTime? CompletedAt { get; init; }
}

/// <summary>Chi tiết một lần chạy đánh giá: thêm từng CV so với nhãn HR.</summary>
public sealed class PromptTestDetailDto : PromptTestSummaryDto
{
    public List<PromptTestItemDto> Items { get; init; } = [];
}

public sealed class PromptTestItemDto
{
    public int ApplicationId { get; init; }
    public int? EvaluationId { get; init; }      // để mở trang chi tiết CV (B9)
    public string? CandidateName { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string JobTitle { get; init; } = string.Empty;

    public bool HrIsPass { get; init; }
    public string? HrNote { get; init; }

    public string Status { get; init; } = string.Empty;   // Pending | Completed | Failed
    public bool? PredictedPass { get; init; }
    public decimal? FinalScore { get; init; }
    public string? FitLabel { get; init; }
    public string? ScoreSource { get; init; }
    public string? Reasoning { get; init; }

    /// <summary>null: chưa chấm xong / lỗi; true: giống HR; false: khác HR.</summary>
    public bool? IsMatch { get; init; }
    /// <summary>"Missed" (bỏ sót) | "WrongPick" (chọn nhầm) | null</summary>
    public string? MismatchType { get; init; }
}

// =====================================================================================
// A15: bộ CV đánh giá và nhãn HR
// =====================================================================================

public sealed class CreateEvaluationSetRequest
{
    public string Name { get; set; } = string.Empty;          // NVARCHAR(150), không trùng
    public string? Description { get; set; }                   // NVARCHAR(500)
}

/// <summary>Một nhãn HR (bảng HrLabels) kèm thông tin hồ sơ để hiển thị.</summary>
public sealed class HrLabelDto
{
    public int ApplicationId { get; init; }
    public string? CandidateName { get; init; }
    public string FileName { get; init; } = string.Empty;
    public int JobId { get; init; }
    public string JobTitle { get; init; } = string.Empty;
    public bool IsPass { get; init; }
    public string? Note { get; init; }
    public string LabeledBy { get; init; } = string.Empty;
    public DateTime LabeledAt { get; init; }                   // UTC
}

public sealed class EvaluationSetDetailDto
{
    public int EvaluationSetId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public DateTime CreatedAt { get; init; }                   // UTC
    public int LabelCount { get; init; }
    public int PassCount { get; init; }
    public int RunCount { get; init; }                         // số lần đã chạy đánh giá trên bộ này
    public List<HrLabelDto> Labels { get; init; } = [];
}

/// <summary>Hồ sơ có thể đưa vào bộ đánh giá (để giao diện cho chọn).</summary>
public sealed class LabelCandidateDto
{
    public int ApplicationId { get; init; }
    public string? CandidateName { get; init; }
    public string FileName { get; init; } = string.Empty;
    public int JobId { get; init; }
    public string JobTitle { get; init; } = string.Empty;
    public string ExtractionStatus { get; init; } = string.Empty;   // Failed: không nên đưa vào bộ đánh giá
    public bool? CurrentLabel { get; init; }                         // null: chưa gán nhãn trong bộ này
}

public sealed class HrLabelInput
{
    public int ApplicationId { get; set; }
    [System.Text.Json.Serialization.JsonRequired]
    public bool IsPass { get; set; }
    public string? Note { get; set; }                          // NVARCHAR(500)
}

public sealed class SaveHrLabelsRequest
{
    public string LabeledBy { get; set; } = string.Empty;      // NVARCHAR(100), ví dụ "HR - Trần Thu Hà"
    public List<HrLabelInput> Labels { get; set; } = [];
}

public sealed class SaveHrLabelsResult
{
    // A15 invalidates stale stored accuracy; recalculation belongs to A16.
    public List<int> InvalidatedRunIds { get; init; } = [];
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Unchanged { get; init; }
    public int LabelCount { get; init; }                       // tổng nhãn của bộ sau khi lưu
    public int PassCount { get; init; }
    public List<int> RecalculatedRunIds { get; init; } = [];   // các lần chạy đã tính lại tỷ lệ đúng vì nhãn đổi
    public List<string> Warnings { get; init; } = [];
}

// =====================================================================================
// A17: so sánh hai phiên bản prompt (hai lần chạy PromptTest trên CÙNG bộ CV)
// =====================================================================================

public sealed class PromptCompareDto
{
    public PromptTestSummaryDto Base { get; init; } = new();        // bản gốc (thường là bản đang dùng)
    public PromptTestSummaryDto Candidate { get; init; } = new();   // bản muốn thử

    public decimal? AccuracyDelta { get; init; }    // Candidate − Base, điểm phần trăm
    public int CorrectDelta { get; init; }
    public int MissedDelta { get; init; }           // âm là tốt (bỏ sót ít hơn)
    public int WrongPickDelta { get; init; }        // âm là tốt

    public int FixedCount { get; init; }            // bản gốc sai, bản thử đúng
    public int BrokenCount { get; init; }           // bản gốc đúng, bản thử sai
    public int UnchangedCount { get; init; }
    public int UnscoredCount { get; init; }         // một trong hai lần không chấm được CV này

    /// <summary>Better | Worse | Same | NotComparable</summary>
    public string Verdict { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;           // một câu tiếng Việt cho quản lý
    public bool IsFairComparison { get; init; }                     // cùng bộ CV, model, temperature, trọng số, ngưỡng
    public List<string> Warnings { get; init; } = [];

    public List<PromptCompareItemDto> Items { get; init; } = [];    // CV thay đổi trước, rồi tới CV giữ nguyên
}

public sealed class PromptCompareItemDto
{
    public int ApplicationId { get; init; }
    public string? CandidateName { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string JobTitle { get; init; } = string.Empty;
    public bool HrIsPass { get; init; }
    public string? HrNote { get; init; }

    public int? BaseEvaluationId { get; init; }
    public bool? BasePredictedPass { get; init; }
    public decimal? BaseScore { get; init; }
    public bool? BaseIsMatch { get; init; }

    public int? CandidateEvaluationId { get; init; }
    public bool? CandidatePredictedPass { get; init; }
    public decimal? CandidateScore { get; init; }
    public bool? CandidateIsMatch { get; init; }

    public decimal? ScoreDelta { get; init; }
    /// <summary>Fixed | Broken | Unchanged | Unscored</summary>
    public string Change { get; init; } = string.Empty;
}
