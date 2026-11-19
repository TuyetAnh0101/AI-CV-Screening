namespace SmartCv.Core.Entities;

/// <summary>
/// SCApp v2: thành viên của một bộ CV đánh giá (bảng EvaluationSetMembers, khóa EvaluationSetId + ApplicationId).
/// Tách khỏi HrLabels: một hồ sơ có thể nằm trong bộ nhưng chưa được HR đánh giá ("Chưa đánh giá").
/// Đơn vị là ApplicationId (một CV ứng tuyển một Job), không phải CvId.
/// </summary>
public class EvaluationSetMember
{
    public int EvaluationSetId { get; set; }
    public int ApplicationId { get; set; }
    public string AddedBy { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; }

    public EvaluationSet EvaluationSet { get; set; } = null!;
    public Application Application { get; set; } = null!;
}
