namespace SmartCv.Core.Entities;

/// <summary>Bảng Applications - Một CV ứng tuyển vào một job.</summary>
public class Application
{
    public int ApplicationId { get; set; }
    public Guid CvId { get; set; }
    public int JobId { get; set; }
    public DateTime CreatedAt { get; set; }

    public Cv Cv { get; set; } = null!;
    public Job Job { get; set; } = null!;
    public ICollection<Evaluation> Evaluations { get; set; } = new List<Evaluation>();
    public ICollection<HrLabel> HrLabels { get; set; } = new List<HrLabel>();
}
