using System.Security.Claims;
using Microsoft.AspNetCore.Components.Routing;

namespace SmartCv.Web.Helpers;

/// <summary>Một mục menu. <see cref="AdminOnly"/>: chỉ hiện với Admin (trang đó cũng phải có [Authorize(Policy = AdminPolicy)]).</summary>
public sealed record NavItem(string Text, string Href, string Icon, NavLinkMatch Match = NavLinkMatch.Prefix, bool AdminOnly = false);

public sealed record NavGroup(string Title, IReadOnlyList<NavItem> Items);

/// <summary>
/// B28: danh mục menu trái theo vai trò (Figma "SC v2"). Một nguồn cho NavMenu và cho nút "quay lại" của trang 403.
///
/// Đối chiếu A29: mọi trang dưới đây HR và Admin đều MỞ được (API đọc của các trang là chính sách nghiệp vụ chung); khác biệt
/// nằm ở các trang quản trị (xem AccessRules). /jobs/new và /jobs/{id} dùng quyền nghiệp vụ Admin/HR.
/// B29 thêm "Tài khoản" (/users) với AdminOnly: true. Nhóm không còn mục nào với vai trò hiện tại thì bị ẩn cả nhóm.
/// </summary>
public static class AppNavigation
{
    public const string AdminHome = "/admin/dashboard";
    public const string RecruitmentHome = "/recruitment";
    public const string DashboardHome = "/dashboard";
    /// <summary>B51: "trang chủ" của người đã đăng nhập — Dashboard (không phải Landing "/").</summary>
    public static string HomeFor(ClaimsPrincipal? user) => AccessRules.IsAdmin(user) ? AdminHome : DashboardHome;
    public static IReadOnlyList<NavGroup> Groups { get; } =
    [
        new("Tổng quan",
        [
            new("Bảng điều khiển", "dashboard", "M3 11 12 3l9 8v10h-6v-6H9v6H3z", NavLinkMatch.All),
            new("Quyền truy cập", "permissions", "M12 2 4 5v6c0 5 3.4 9.1 8 11 4.6-1.9 8-6 8-11V5z"),
        ]),
        new("Tuyển dụng",
        [
            new("Tải hồ sơ lên", "upload", "M12 3 6 9h4v6h4V9h4zM4 17h16v4H4z"),
            // B24: một mục duy nhất cho người và file CV (thay "Kho CV"); /candidates/{id} cũng sáng mục này
            new("Ứng viên", "candidates", "M9 11a4 4 0 1 1 0-8a4 4 0 0 1 0 8zm-7 9a7 7 0 0 1 14 0v1H2zm15-9a3 3 0 1 0 0-6v6zm1 2a5 5 0 0 1 4 5v3h-3v-1a8 8 0 0 0-1-4z"),
            new("Kết quả", "results", "M4 20V10h3v10zm6.5 0V4h3v16zM17 20v-7h3v7z"),
            new("Vị trí tuyển dụng", "jobs", "M3 7h18v14H3zm5-4h8v4h-2V5h-4v2H8zm-5 9h18v2H3z"),
            new("Lịch sử lượt chấm", "runs", "M12 3a9 9 0 1 1 0 18a9 9 0 1 1 0-18zM11 7h2v5l3 3-1 1-4-4z"),
        ]),
        new("Đánh giá AI",
        [
            new("System prompt", "prompts", "M4 4h16v12H8l-4 4zm4 5h8V7H8zm0 4h5v-2H8z"),
            // B25: bộ CV tham chiếu + nhãn HR tách khỏi trang System prompt
            new("Bộ dữ liệu đánh giá", "evaluation-sets", "M4 5h3v3H4zm5 .5h11v2H9zM4 10.5h3v3H4zm5 .5h11v2H9zM4 16h3v3H4zm5 .5h11v2H9z"),
        ]),
        new("Quản trị",
        [
            // B29: quản lý tài khoản (A30 /api/users chỉ Admin)
            new("Trạng thái hệ thống", "admin/system-health", "M4 4h16v16H4zm4 8h8m-4-4v8", AdminOnly: true),
            new("Sao lưu hệ thống", "admin/backups", "M12 2 4 5v6c0 5 3.4 9.1 8 11 4.6-1.9 8-6 8-11V5z", AdminOnly: true),
            new("Workspace & quota", "admin/workspaces", "M4 4h16v16H4zm4 4h8m-8 5h8m-8 5h8", AdminOnly: true),
            new("Giám sát AI", "admin/ai-usage", "M5 17l4-6 4 3 5-8M3 20h18", AdminOnly: true),
            new("Kết nối Google & AI", "admin/integrations", "M4 12h4m8 0h4M12 4v4m0 8v4M8 8l8 8", AdminOnly: true),
            new("Tài khoản", "users", "M9 11a4 4 0 1 1 0-8a4 4 0 0 1 0 8zm-7 9a7 7 0 0 1 11.2-5.6A6 6 0 0 0 13 16v4H2zm16.5-7 3.5 1.3v2.6c0 2.4-1.5 4.5-3.5 5.1-2-.6-3.5-2.7-3.5-5.1v-2.6z", AdminOnly: true),
            new("Nhật ký hoạt động", "admin/activity-logs", "M6 3h12v18H6zM9 7h6M9 11h6M9 15h4", AdminOnly: true),
        ]),
    ];

    /// <summary>Các nhóm và mục mà người dùng này được thấy; không bao giờ có nhóm rỗng. Chưa dùng được ứng dụng → rỗng.</summary>
    public static IReadOnlyList<NavGroup> For(ClaimsPrincipal? user) => RoleBasedNavigation.For(user);

    /// <summary>
    /// Mục menu "cha" của một đường dẫn (vd. "/jobs/new" → mục "Vị trí tuyển dụng") mà người dùng được mở, khác trang hiện tại.
    /// Dùng cho nút quay lại an toàn trên trang 403: luôn là đường dẫn trong ứng dụng, không dùng lịch sử trình duyệt.
    /// </summary>
    public static NavItem? ParentFor(string? relativePath, ClaimsPrincipal? user)
    {
        var path = (relativePath ?? "").Split('?', '#')[0].Trim('/');
        var first = path.Split('/')[0];
        if (first.Length == 0 || path.Equals(first, StringComparison.OrdinalIgnoreCase)) return null;
        return For(user).SelectMany(g => g.Items)
            .FirstOrDefault(i => i.Href.Length > 0 && i.Href.Equals(first, StringComparison.OrdinalIgnoreCase));
    }
}
