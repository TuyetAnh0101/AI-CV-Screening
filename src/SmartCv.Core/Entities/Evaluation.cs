namespace SmartCv.Core.Entities;

/// <summary>Bảng Evaluations - Kết quả chấm của một hồ sơ trong một lượt chấm (bảng trung tâm).</summary>
public class Evaluation
{
    public int EvaluationId { get; set; }
    public int RunId { get; set; }
    public int ApplicationId { get; set; }
    public decimal? KeywordScore { get; set; }
    public decimal? AiScore { get; set; }
    public decimal? FinalScore { get; set; }
    public string? ScoreSource { get; set; }      // Combined | KeywordOnly
    public string? FitLabel { get; set; }
    public bool? PredictedPass { get; set; }
    public decimal? YearsExperience { get; set; }
    public string? Reasoning { get; set; }
    public string? AiRawResponse { get; set; }
    public string Status { get; set; } = "Pending"; // Pending | Completed | Failed
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }

    public EvaluationRun Run { get; set; } = null!;
    public Application Application { get; set; } = null!;
    public ICollection<EvaluationSkill> Skills { get; set; } = new List<EvaluationSkill>();
}
