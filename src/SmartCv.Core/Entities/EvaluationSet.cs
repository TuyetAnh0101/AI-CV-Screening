namespace SmartCv.Core.Entities;

/// <summary>Bảng EvaluationSets - Bộ CV dùng để đánh giá prompt.</summary>
public class EvaluationSet
{
    public int EvaluationSetId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>SCApp v2: mục tiêu của bộ (ví dụ "Kiểm tra prompt v3 với Backend junior").</summary>
    public string? Purpose { get; set; }
    /// <summary>SCApp v2: vị trí tuyển dụng chính của bộ (không bắt buộc; bộ vẫn có thể chứa hồ sơ của Job khác).</summary>
    public int? JobId { get; set; }
    /// <summary>
    /// SCApp v2: tăng 1 mỗi khi thành viên hoặc nhãn HR thay đổi. Mỗi lượt PromptTest lưu số này lúc tạo,
    /// nên giao diện biết bộ đã đổi sau lượt chạy và cảnh báo khi so sánh hai lượt khác phiên bản bộ.
    /// </summary>
    public int Revision { get; set; } = 1;
    public DateTime? UpdatedAt { get; set; }

    public Job? Job { get; set; }
    public ICollection<EvaluationSetMember> Members { get; set; } = new List<EvaluationSetMember>();
    public ICollection<HrLabel> HrLabels { get; set; } = new List<HrLabel>();
    public ICollection<EvaluationRun> EvaluationRuns { get; set; } = new List<EvaluationRun>();
}
