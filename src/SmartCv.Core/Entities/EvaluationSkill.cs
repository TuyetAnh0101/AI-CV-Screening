namespace SmartCv.Core.Entities;

/// <summary>Bảng EvaluationSkills - Kỹ năng đạt hoặc thiếu trong một lần chấm.</summary>
public class EvaluationSkill
{
    public int EvaluationId { get; set; }
    public int JobId { get; set; }
    public string SkillCode { get; set; } = string.Empty;
    public bool IsMatched { get; set; }
    public string? Evidence { get; set; }

    public Evaluation Evaluation { get; set; } = null!;
    public JobSkill JobSkill { get; set; } = null!;
}
