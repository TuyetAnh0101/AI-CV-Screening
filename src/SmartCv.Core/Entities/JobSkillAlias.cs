namespace SmartCv.Core.Entities;

/// <summary>Bảng JobSkillAliases - Từ khóa tương đương của kỹ năng (khóa chính JobId + SkillCode + Alias).</summary>
public class JobSkillAlias
{
    public int JobId { get; set; }
    public string SkillCode { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;

    public JobSkill JobSkill { get; set; } = null!;
}
