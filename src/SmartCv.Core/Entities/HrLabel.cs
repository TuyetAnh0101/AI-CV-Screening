namespace SmartCv.Core.Entities;

/// <summary>Bảng HrLabels - Nhãn Đạt/Không đạt của HR (khóa chính EvaluationSetId + ApplicationId).</summary>
public class HrLabel
{
    public int EvaluationSetId { get; set; }
    public int ApplicationId { get; set; }
    public bool IsPass { get; set; }
    public string? Note { get; set; }
    public string LabeledBy { get; set; } = string.Empty;
    public DateTime LabeledAt { get; set; }

    public EvaluationSet EvaluationSet { get; set; } = null!;
    public Application Application { get; set; } = null!;
}
