using SmartCv.Core.Workspaces;

namespace SmartCv.Core.Entities;

/// <summary>Bảng Applications - Một CV ứng tuyển vào một job.</summary>
public class Application : IWorkspaceOwned
{
    public int ApplicationId { get; set; }
    /// <summary>Workspace sở hữu. Mặc định chưa gán (UnassignedWorkspaceId = -1); SmartCvDbContext (A52) tự đóng dấu workspace hiện hành khi lưu.</summary>
    public int WorkspaceId { get; set; } = WorkspaceKinds.UnassignedWorkspaceId;
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
    public ICollection<EvaluationSetMember> EvaluationSetMemberships { get; set; } = new List<EvaluationSetMember>();
    public ICollection<ApplicationFlag> Flags { get; set; } = new List<ApplicationFlag>();
    public ICollection<ApplicationEvent> Events { get; set; } = new List<ApplicationEvent>();
}
