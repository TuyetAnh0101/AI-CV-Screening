namespace SmartCv.Core.Entities;

// Global infrastructure entity: KHÔNG implement IWorkspaceOwned.
// ClaimId + LeaseUntilUtc nhằm chống 2 worker lấy trùng dòng; Status giữ đúng ba giá trị.
public sealed class EmailOutboxItem
{
    public long EmailOutboxId { get; set; }
    public int? UserId { get; set; }
    public User? User { get; set; }
    public string ToAddress { get; set; } = "";
    public string Template { get; set; } = "";
    public string? ProtectedPayload { get; set; }
    public string Status { get; set; } = "Pending";
    public byte Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public string? LastError { get; set; }
    public Guid? ClaimId { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
}
