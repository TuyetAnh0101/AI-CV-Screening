namespace SmartCv.Core.Entities;

/// <summary>SCApp v2: lịch sử một cờ thủ công (Opened | Resolved | Reopened). Chỉ thêm, không sửa.</summary>
public class ApplicationFlagEvent
{
    public long EventId { get; set; }
    public int FlagId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }

    public ApplicationFlag Flag { get; set; } = null!;
}
