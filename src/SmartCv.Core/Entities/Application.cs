namespace SmartCv.Core.Entities;

/// <summary>Bảng Applications - Một CV ứng tuyển vào một job.</summary>
public class Application
{
    public int ApplicationId { get; set; }
    public Guid CvId { get; set; }
    public int JobId { get; set; }
    public DateTime CreatedAt { get; set; }

    public string Stage { get; set; } = Constants.ApplicationStages.New;
    public string? RejectReason { get; set; }
    public DateTime? StageUpdatedAt { get; set; }
    /// <summary>Changes with every HR decision, including a reason change or a reopened stage.</summary>
    public Guid StageVersion { get; set; } = Guid.NewGuid();

    public Cv Cv { get; set; } = null!;
    public Job Job { get; set; } = null!;
    public ICollection<Evaluation> Evaluations { get; set; } = new List<Evaluation>();
    public ICollection<HrLabel> HrLabels { get; set; } = new List<HrLabel>();
    public ICollection<ApplicationEvent> Events { get; set; } = new List<ApplicationEvent>();
}
