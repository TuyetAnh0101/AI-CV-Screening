namespace SmartCv.Core.Contracts;

// A13: dữ liệu cho trang Kết quả (B7–B11). Dùng chung cho API và giao diện.

/// <summary>Một dòng trong danh sách lượt chấm: GET /api/runs</summary>
public sealed class RunSummaryDto
{
    public int RunId { get; init; }
    public string RunType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string PromptVersionName { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public decimal Percent => TotalItems == 0 ? 100 : Math.Clamp(ProcessedItems * 100m / TotalItems, 0, 100);
    public int TotalItems { get; init; }
    public int ProcessedItems { get; init; }
    public int FailedItems { get; init; }
    public decimal? AccuracyPercent { get; init; }
    public DateTime CreatedAt { get; init; }        // UTC
    public DateTime? CompletedAt { get; init; }
    public List<JobCountDto> Jobs { get; init; } = [];   // các job có trong lượt chấm
}

public sealed class JobCountDto
{
    public int JobId { get; init; }
    public string Title { get; init; } = string.Empty;
    public int Count { get; init; }
}

/// <summary>Bảng xếp hạng một job trong một lượt chấm: GET /api/runs/{id}/ranking?jobId=</summary>
public sealed class RankingResponse
{
    public int RunId { get; init; }
    public string RunStatus { get; init; } = string.Empty;
    public string PromptVersionName { get; init; } = string.Empty;
    public decimal PassThreshold { get; init; }
    public int JobId { get; init; }
    public string JobTitle { get; init; } = string.Empty;
    public List<JobCountDto> Jobs { get; init; } = [];            // để làm ô lọc job (B7)
    public Dictionary<string, int> LevelCounts { get; init; } = new();  // số CV theo mức (biểu đồ B11)
    public int PassCount { get; init; }
    public List<RankingItemDto> Items { get; init; } = [];
}

public sealed class RankingItemDto
{
    public int? Rank { get; init; }               // null: chưa chấm / lỗi (đứng cuối bảng)
    public int EvaluationId { get; init; }
    public int ApplicationId { get; init; }
    public Guid CvId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string? CandidateName { get; init; }
    public decimal? KeywordScore { get; init; }
    public decimal? AiScore { get; init; }
    public decimal? FinalScore { get; init; }
    public string? ScoreSource { get; init; }     // KeywordOnly → giao diện PHẢI đánh dấu rõ
    public string? FitLabel { get; init; }
    public bool? PredictedPass { get; init; }
    public decimal? YearsExperience { get; init; }
    public int MatchedMustHave { get; init; }
    public int TotalMustHave { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
}

/// <summary>Chi tiết kết quả chấm một CV: GET /api/evaluations/{id} (trang B9).</summary>
public sealed class EvaluationDetailDto
{
    public decimal? Gpa { get; init; }
    public int EvaluationId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }

    // Lượt chấm và phiên bản prompt đã dùng (A12)
    public int RunId { get; init; }
    public string RunType { get; init; } = string.Empty;
    public int PromptVersionId { get; init; }
    public string PromptVersionName { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public decimal Temperature { get; init; }
    public decimal KeywordWeight { get; init; }
    public decimal AiWeight { get; init; }
    public decimal PassThreshold { get; init; }

    // CV và job
    public int ApplicationId { get; init; }
    public Guid CvId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public string? CandidateName { get; init; }
    public string? CandidateEmail { get; init; }
    public string? ExtractionMethod { get; init; }
    public int JobId { get; init; }
    public string JobTitle { get; init; } = string.Empty;
    public decimal? JobMinGpa { get; init; }
    public decimal JobMinYearsExperience { get; init; }

    // Điểm
    public int? Rank { get; init; }
    public int RankTotal { get; init; }
    public decimal? KeywordScore { get; init; }
    public decimal? AiScore { get; init; }
    public decimal? FinalScore { get; init; }
    public string? ScoreSource { get; init; }
    public string? FitLabel { get; init; }
    public bool? PredictedPass { get; init; }
    public decimal? YearsExperience { get; init; }
    public string? Reasoning { get; init; }
    public string? AiRawResponse { get; init; }
    public DateTime CreatedAt { get; init; }

    public List<EvaluationSkillDto> Skills { get; init; } = [];
}

public sealed class EvaluationSkillDto
{
    public string SkillCode { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string SkillType { get; init; } = string.Empty;   // MustHave | NiceToHave
    public bool IsMatched { get; init; }
    public string? Evidence { get; init; }
}
