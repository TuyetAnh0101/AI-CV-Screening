namespace SmartCv.Core.Workspaces;

/// <summary>
/// Ngữ cảnh workspace của thao tác hiện tại. Giá trị do server xác lập (JWT đã ký + kiểm tra lại với database, hoặc
/// <c>WorkspaceScope</c> trong code), không bao giờ lấy từ header/query/body của client.
/// </summary>
public interface IWorkspaceContext
{
    /// <summary>Workspace hiện hành; null khi không có workspace hợp lệ (fail-closed) hoặc đang ở system scope.</summary>
    int? WorkspaceId { get; }

    /// <summary>Quyền thành viên trong workspace (Owner/Member) theo database; không phải <c>User.Role</c>.</summary>
    string? Role { get; }

    /// <summary>Internal | Personal.</summary>
    string? Kind { get; }

    /// <summary>
    /// true chỉ trong <c>WorkspaceScope.BeginSystem(reason)</c> (worker/retention đã được cho phép): bỏ lọc workspace.
    /// Request thông thường không bao giờ ở trạng thái này.
    /// </summary>
    bool IsSystemScope { get; }
}
