namespace SmartCv.Core.Entities;

/// <summary>Bảng PromptVersions - Phiên bản system prompt. Chỉ một bản được IsActive = true.</summary>
public class PromptVersion
{
    public int PromptVersionId { get; set; }
    public string VersionName { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string? ChangeNote { get; set; }
    public bool IsActive { get; set; } = false;
    public DateTime CreatedAt { get; set; }

    public ICollection<EvaluationRun> EvaluationRuns { get; set; } = new List<EvaluationRun>();
}
