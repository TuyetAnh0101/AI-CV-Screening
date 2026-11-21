namespace SmartCv.Core.Entities;

/// <summary>
/// A43: nhật ký yêu cầu của ứng viên về dữ liệu (bảng CandidateDataRequests) – bằng chứng đã xử lý.
/// CỐ Ý không có khóa ngoại tới Candidates và không lưu tên / email: sau khi xóa dữ liệu, dòng Candidates biến mất
/// nhưng nhật ký vẫn phải còn để chứng minh đã làm. CandidateId chỉ là số tham chiếu.
/// </summary>
public class CandidateDataRequest : SmartCv.Core.Workspaces.IWorkspaceOwned
{
    public int WorkspaceId { get; set; } = SmartCv.Core.Workspaces.WorkspaceKinds.UnassignedWorkspaceId;
    public long RequestId { get; set; }
    public int CandidateId { get; set; }
    public string RequestType { get; set; } = string.Empty;   // Export | Erase
    public string HandledBy { get; set; } = string.Empty;     // HR xử lý
    public string? Note { get; set; }                          // kênh nhận yêu cầu, ngày ứng viên gửi...
    public string? ResultSummary { get; set; }                 // "Ẩn danh hóa 2 CV, rút 1 hồ sơ"
    public string? CvIds { get; set; }                         // các CvId liên quan, cách nhau dấu phẩy
    public DateTime CreatedAt { get; set; }                    // UTC
}
