namespace SmartCv.Core.Auth;

/// <param name="Workspace">Membership đang có hiệu lực của workspace được hỏi (null nếu không hỏi hoặc không còn thành viên).</param>
/// <param name="WorkspaceActive">false nếu workspace tồn tại nhưng đã bị khóa.</param>
public sealed record UserSessionState(bool IsActive, string SecurityStamp,
    string? Role = null, bool? MustChangePassword = null, string? FullName = null, string? Email = null,
    SmartCv.Core.Workspaces.WorkspaceSessionInfo? Workspace = null, bool WorkspaceActive = true);

public static class SessionRules
{
    public static string? Check(UserSessionState? state, string? tokenStamp)
    {
        if (string.IsNullOrWhiteSpace(tokenStamp)) return "Token thiếu thông tin phiên đăng nhập.";
        if (state is null) return "Tài khoản không còn tồn tại.";
        if (!state.IsActive) return "Tài khoản đã bị khóa.";
        if (string.IsNullOrWhiteSpace(state.SecurityStamp) || !string.Equals(state.SecurityStamp, tokenStamp, StringComparison.Ordinal))
            return "Phiên đăng nhập đã hết hiệu lực.";
        return null;
    }

    public const string WorkspaceRevoked = "Quyền truy cập không gian làm việc đã thay đổi. Hãy đăng nhập lại.";
    public const string WorkspaceClaimInvalid = "Token chứa thông tin không gian làm việc không hợp lệ.";

    /// <summary>Kết luận cho claim smartcv_ws: không còn membership hoặc workspace bị khóa => phiên không còn hợp lệ.</summary>
    public static string? CheckWorkspace(UserSessionState state, int tokenWorkspaceId) =>
        state.Workspace is { } w && w.Id == tokenWorkspaceId && state.WorkspaceActive ? null : WorkspaceRevoked;
}
