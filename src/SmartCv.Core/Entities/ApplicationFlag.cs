namespace SmartCv.Core.Entities;

/// <summary>
/// SCApp v2: cờ "Cần kiểm tra lại" do HR CHỦ ĐỘNG gắn cho một hồ sơ (bảng ApplicationFlags).
/// Khác với cảnh báo tự động (ReviewFlags, tính lại từ kết quả chấm, không lưu).
/// Gắn hoặc xử lý cờ KHÔNG đổi giai đoạn tuyển dụng. Mỗi hồ sơ có tối đa một cờ đang mở.
/// </summary>
public class ApplicationFlag
{
    public int FlagId { get; set; }
    public int ApplicationId { get; set; }
    public string Reason { get; set; } = string.Empty;    // ManualFlagReasons
    public string? Note { get; set; }
    public string Status { get; set; } = Constants.ManualFlagStatuses.Open;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolutionNote { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Đổi sau mỗi thao tác: hai HR cùng xử lý một cờ thì người sau nhận 409.</summary>
    public Guid Version { get; set; } = Guid.NewGuid();

    public Application Application { get; set; } = null!;
    public ICollection<ApplicationFlagEvent> Events { get; set; } = new List<ApplicationFlagEvent>();
}
