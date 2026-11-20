using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;

namespace SmartCv.Web.Helpers;

/// <summary>B29: chữ hiển thị và điều kiện thao tác trên trang Tài khoản. Hàm thuần → unit test được.</summary>
public static class UserText
{
    public static string Role(string? role) => role switch { UserRoles.Admin => "Quản trị viên", UserRoles.Hr => "Nhân sự (HR)", _ => "Không xác định" };

    public static string RoleCss(string? role) => role == UserRoles.Admin ? "role-admin" : "role-hr";

    public static string Status(UserDto u) => u.IsActive ? "Đang hoạt động" : "Đã khóa";

    public static string StatusCss(UserDto u) => u.IsActive ? "is-active" : "is-locked";

    /// <summary>Trạng thái mật khẩu: tài khoản mới / vừa đặt lại phải đổi mật khẩu khi đăng nhập.</summary>
    public static string Password(UserDto u) => u.MustChangePassword ? "Chờ đổi mật khẩu" : "Đã đặt mật khẩu";

    public static string LastLogin(DateTime? utc) => utc is { } value ? RankingView.VnTime(value) : "Chưa đăng nhập";

    /// <summary>Lý do KHÔNG được khóa (làm mờ nút + giải thích), null = được. Cùng quy tắc với API (UserRules.CanLock).</summary>
    public static string? LockBlockedReason(UserDto target, int? meUserId, int? activeAdmins)
    {
        if (!target.IsActive) return null;
        if (meUserId is null) return "Chưa xác định được tài khoản đang đăng nhập.";
        if (target.UserId == meUserId) return "Không tự khóa tài khoản của chính mình.";
        if (target.Role == UserRoles.Admin && activeAdmins is null) return "Đang kiểm tra số Admin đang hoạt động.";
        return UserRules.CanLock(meUserId.Value, Snapshot(target), activeAdmins ?? int.MaxValue);
    }

    /// <summary>Lý do KHÔNG được đổi vai trò sang <paramref name="newRole"/>, null = được (UserRules.CanChangeRole).</summary>
    public static string? RoleBlockedReason(UserDto target, string newRole, int? meUserId, int? activeAdmins)
    {
        if (target.Role == newRole) return null;
        if (meUserId is null) return "Chưa xác định được tài khoản đang đăng nhập.";
        if (target.Role == UserRoles.Admin && activeAdmins is null) return "Đang kiểm tra số Admin đang hoạt động.";
        return UserRules.CanChangeRole(meUserId.Value, Snapshot(target), newRole, activeAdmins ?? int.MaxValue);
    }

    /// <summary>Đặt lại mật khẩu chỉ cho HR (A30 chặn Admin: Admin tự đổi bằng mật khẩu hiện tại).</summary>
    public static string? ResetBlockedReason(UserDto target) =>
        target.Role == UserRoles.Hr ? null : "Tài khoản Admin tự đổi mật khẩu bằng mật khẩu hiện tại; không đặt lại từ đây.";

    private static UserSnapshot Snapshot(UserDto u) => new(u.UserId, u.Role, u.IsActive);
}
