namespace SmartCv.Core.Entities;

/// <summary>Bảng EvaluationSets - Bộ CV dùng để đánh giá prompt.</summary>
public class EvaluationSet
{
    public int EvaluationSetId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }

    public ICollection<HrLabel> HrLabels { get; set; } = new List<HrLabel>();
    public ICollection<EvaluationRun> EvaluationRuns { get; set; } = new List<EvaluationRun>();
}
