namespace SmartCv.Core.Entities;

/// <summary>Bảng EvaluationRuns - Một lượt chấm (Screening hoặc PromptTest).</summary>
public class EvaluationRun
{
    public int RunId { get; set; }
    public string RunType { get; set; } = string.Empty;   // Screening | PromptTest
    public int PromptVersionId { get; set; }
    public int? EvaluationSetId { get; set; }             // PromptTest: bắt buộc; Screening: để null
    public string ModelName { get; set; } = string.Empty;
    public decimal Temperature { get; set; }
    public decimal KeywordWeight { get; set; }
    public decimal AiWeight { get; set; }
    public decimal PassThreshold { get; set; } = 70;
    public string Status { get; set; } = "Queued";        // Queued | Running | Completed | Failed
    public int TotalItems { get; set; }
    public int ProcessedItems { get; set; } = 0;
    public int FailedItems { get; set; } = 0;
    public decimal? AccuracyPercent { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public PromptVersion PromptVersion { get; set; } = null!;
    public EvaluationSet? EvaluationSet { get; set; }
    public ICollection<Evaluation> Evaluations { get; set; } = new List<Evaluation>();
}
