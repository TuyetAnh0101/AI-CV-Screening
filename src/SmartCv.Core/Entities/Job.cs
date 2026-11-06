namespace SmartCv.Core.Entities;

/// <summary>Bảng Jobs - Vị trí tuyển dụng.</summary>
public class Job
{
    public int JobId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal? MinGpa { get; set; }
    public decimal MinYearsExperience { get; set; } = 0;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public ICollection<JobSkill> Skills { get; set; } = new List<JobSkill>();
    public ICollection<Application> Applications { get; set; } = new List<Application>();
}
