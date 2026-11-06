namespace SmartCv.Core.Entities;

/// <summary>Bảng JobSkills - Kỹ năng của job (thực thể yếu, khóa chính JobId + SkillCode).</summary>
public class JobSkill
{
    public int JobId { get; set; }
    public string SkillCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string SkillType { get; set; } = string.Empty; // MustHave | NiceToHave

    public Job Job { get; set; } = null!;
    public ICollection<JobSkillAlias> Aliases { get; set; } = new List<JobSkillAlias>();
    public ICollection<EvaluationSkill> EvaluationSkills { get; set; } = new List<EvaluationSkill>();
}
