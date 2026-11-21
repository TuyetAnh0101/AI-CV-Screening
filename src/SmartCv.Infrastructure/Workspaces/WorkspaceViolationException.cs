namespace SmartCv.Infrastructure.Workspaces;

/// <summary>
/// Một thao tác ghi vi phạm ranh giới workspace (thiếu workspace, sai workspace, hoặc cố đổi WorkspaceId).
/// Đây là lỗi bất biến phía server (client không chọn được workspace), nên API trả 500 chung. Thông điệp cố ý không chứa
/// mã workspace, mã bản ghi hay dữ liệu: chi tiết chỉ nằm trong log của server.
/// </summary>
public sealed class WorkspaceViolationException(string entityType, string operation)
    : InvalidOperationException("Thao tác bị từ chối vì vi phạm ranh giới không gian làm việc.")
{
    public string EntityType { get; } = entityType;
    public string Operation { get; } = operation;
}
