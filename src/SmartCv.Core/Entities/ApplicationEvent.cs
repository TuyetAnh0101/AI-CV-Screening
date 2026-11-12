namespace SmartCv.Core.Entities;

/// <summary>
/// A35: một lần đổi giai đoạn hồ sơ (bảng ApplicationEvents). CHỈ THÊM, không sửa, không xóa
/// (database có trigger chặn UPDATE / DELETE). Dùng để biết ai quyết định gì, lúc nào, vì sao.
/// </summary>
public class ApplicationEvent
{
    public long EventId { get; set; }
    public int ApplicationId { get; set; }
    public string FromStage { get; set; } = string.Empty;
    public string ToStage { get; set; } = string.Empty;
    public string? RejectReason { get; set; }
    public string? Note { get; set; }
    public string Actor { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public Application Application { get; set; } = null!;
}
