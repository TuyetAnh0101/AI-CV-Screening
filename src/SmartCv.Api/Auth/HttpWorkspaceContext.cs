using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Workspaces;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Workspaces;

namespace SmartCv.Api.Auth;

/// <summary>
/// Workspace của request hiện tại. Chỉ đọc kết quả mà <see cref="UserSessionValidator"/> đã xác nhận với database
/// (đặt vào HttpContext.Items sau khi kiểm tra chữ ký JWT + membership + workspace Active). Không bao giờ đọc header, query hay body.
/// Nếu code đã mở <see cref="WorkspaceScope"/> (worker, test) thì scope đó được ưu tiên.
/// </summary>
public sealed class HttpWorkspaceContext(IHttpContextAccessor accessor) : IWorkspaceContext
{
    public const string ItemKey = "SCApp.Workspace.Validated";

    private WorkspaceSessionInfo? Validated => accessor.HttpContext?.Items.TryGetValue(ItemKey, out var value) == true ? value as WorkspaceSessionInfo : null;

    public int? WorkspaceId => WorkspaceScope.IsActive ? WorkspaceScope.Ambient.WorkspaceId : Validated?.Id;
    public string? Role => WorkspaceScope.IsActive ? WorkspaceScope.Ambient.Role : Validated?.Role;
    public string? Kind => WorkspaceScope.IsActive ? WorkspaceScope.Ambient.Kind : Validated?.Kind;
    public bool IsSystemScope => WorkspaceScope.Ambient.IsSystemScope;

    public static bool HasValidatedWorkspace(HttpContext http) => http.Items.TryGetValue(ItemKey, out var value) && value is WorkspaceSessionInfo;
    public static WorkspaceSessionInfo? Get(HttpContext http) => http.Items.TryGetValue(ItemKey, out var value) ? value as WorkspaceSessionInfo : null;
}

/// <summary>Chọn workspace khi cấp token. Không tạo membership, không gán tài khoản thiếu membership vào workspace nào.</summary>
public static class WorkspaceSelection
{
    public static async Task<WorkspaceSessionInfo?> ResolveAsync(SmartCvDbContext db, int userId, CancellationToken ct)
    {
        // Chỉ membership còn hiệu lực trong workspace đang Active. WorkspaceMembers/Workspaces là bảng toàn cục (không lọc theo workspace).
        var memberships = await db.WorkspaceMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.Workspace.IsActive)
            .Select(m => new WorkspaceSessionInfo(m.WorkspaceId, m.Workspace.Name, m.Workspace.Kind, m.Role, m.Workspace.OwnerUserId))
            .ToListAsync(ct);
        return WorkspaceSelectionRules.Choose(memberships);
    }
}
