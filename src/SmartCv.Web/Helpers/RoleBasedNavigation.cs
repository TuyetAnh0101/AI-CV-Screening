using System.Security.Claims;
using Microsoft.AspNetCore.Components.Routing;
using SmartCv.Core.Auth;

namespace SmartCv.Web.Helpers;

/// <summary>
/// One UI-only, verified-session navigation model. API authorization remains authoritative.
/// User.Role=HR is not sufficient to identify Personal Owner: verified workspace kind/role is required.
/// No links to unimplemented download/export endpoints are advertised.
/// </summary>
public enum WorkspacePersona { None, SystemAdmin, InternalHr, PersonalOwner }

public static class RoleBasedNavigation
{
    private const string DashboardIcon = "M3 11 12 3l9 8v10h-6v-6H9v6H3z";
    private const string JobIcon = "M3 7h18v14H3zm5-4h8v4h-2V5h-4v2H8zm-5 9h18v2H3z";
    private const string CvIcon = "M12 3 6 9h4v6h4V9h4zM4 17h16v4H4z";
    private const string ResultsIcon = "M4 20V10h3v10zm6.5 0V4h3v16zM17 20v-7h3v7z";
    private const string ClockIcon = "M12 3a9 9 0 1 1 0 18a9 9 0 1 1 0-18zM11 7h2v5l3 3-1 1-4-4z";
    private const string UsersIcon = "M9 11a4 4 0 1 1 0-8a4 4 0 0 1 0 8zm-7 9a7 7 0 0 1 14 0v1H2zm15-9a3 3 0 1 0 0-6v6zm1 2a5 5 0 0 1 4 5v3h-3v-1a8 8 0 0 0-1-4z";
    private const string ShieldIcon = "M12 2 4 5v6c0 5 3.4 9.1 8 11 4.6-1.9 8-6 8-11V5z";
    private const string ReportIcon = "M5 3h10l4 4v14H5zm9 1v4h4M8 12h8M8 16h8";
    private const string PromptIcon = "M4 4h16v12H8l-4 4zm4 5h8V7H8zm0 4h5v-2H8z";
    private const string DataIcon = "M4 5h3v3H4zm5 .5h11v2H9zM4 10.5h3v3H4zm5 .5h11v2H9zM4 16h3v3H4zm5 .5h11v2H9z";
    private const string StatusIcon = "M4 4h16v16H4zm4 8h8m-4-4v8";
    private const string QuotaIcon = "M4 4h16v16H4zm4 4h8m-8 5h8m-8 5h8";
    private const string AiIcon = "M5 17l4-6 4 3 5-8M3 20h18";
    private const string AccountIcon = "M12 12a4 4 0 1 0 0-8a4 4 0 0 0 0 8zM4 21a8 8 0 0 1 16 0";
    private const string AuditIcon = "M6 3h12v18H6zM9 7h6M9 11h6M9 15h4";

    public static WorkspacePersona PersonaFor(ClaimsPrincipal? user)
    {
        if (AccessRules.IsAdmin(user)) return WorkspacePersona.SystemAdmin;
        if (!AccessRules.CanOperateBusiness(user)) return WorkspacePersona.None;
        var kind = user!.FindFirst(AccessRules.WorkspaceKindClaim)?.Value;
        var role = user.FindFirst(AccessRules.WorkspaceRoleClaim)?.Value;
        if (kind == "Personal" && role == "Owner") return WorkspacePersona.PersonalOwner;
        if (kind == "Internal" && role is "Owner" or "Member") return WorkspacePersona.InternalHr;
        return WorkspacePersona.None;
    }

    // Used only AFTER AuthSession has been verified. Never trusted for API access.
    public static string Destination(string? raw, string? accountRole, string? workspaceKind, string? workspaceRole)
    {
        var safe = AuthText.SafeReturnUrl(raw);
        var path = safe.Split('?', '#')[0].TrimEnd('/').ToLowerInvariant();
        var isAdmin = accountRole == UserRoles.Admin;
        var business = accountRole == UserRoles.Hr &&
            ((workspaceKind == "Internal" && workspaceRole is "Owner" or "Member") ||
             (workspaceKind == "Personal" && workspaceRole == "Owner"));
        if (isAdmin)
        {
            // The Admin cannot use business pages, including a deep link carried in returnUrl.
            return IsBusinessPath(path) || path is "/dashboard" or "/recruitment"
                ? AppNavigation.AdminHome : safe == AuthText.DashboardPath ? AppNavigation.AdminHome : safe;
        }
        if (business)
        {
            // Personal Owner and HR do not acquire Admin access from a saved returnUrl.
            return IsAdminPath(path) ? AppNavigation.DashboardHome : safe;
        }
        // A verified but mis-provisioned HR account can still inspect its own account.
        return "/account";
    }

    private static bool IsAdminPath(string path) =>
        path == "/users" || path.StartsWith("/users/", StringComparison.Ordinal) ||
        path == "/admin" || path.StartsWith("/admin/", StringComparison.Ordinal);

    private static bool IsBusinessPath(string path) =>
        new[] { "/upload", "/candidates", "/cvs", "/jobs", "/results", "/runs", "/prompts", "/evaluation-sets", "/reports/recruitment" }
            .Any(prefix => path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal));

    public static string TopbarTitle(ClaimsPrincipal? user) => PersonaFor(user) switch
    {
        WorkspacePersona.SystemAdmin => "Quản trị & vận hành SCApp",
        WorkspacePersona.InternalHr => "Trung tâm tuyển dụng nội bộ",
        WorkspacePersona.PersonalOwner => "Không gian tuyển dụng cá nhân",
        _ => "SCApp · Xác minh quyền truy cập"
    };

    public static string DisplayRole(ClaimsPrincipal? user) => PersonaFor(user) switch
    {
        WorkspacePersona.SystemAdmin => "Quản trị viên hệ thống",
        WorkspacePersona.InternalHr => "Nhân sự nội bộ (HR)",
        WorkspacePersona.PersonalOwner => "Chủ workspace cá nhân",
        _ => "Tài khoản chưa có workspace hợp lệ"
    };

    public static string PersonaLabel(ClaimsPrincipal? user) => PersonaFor(user) switch
    {
        WorkspacePersona.SystemAdmin => "VẬN HÀNH HỆ THỐNG",
        WorkspacePersona.InternalHr => "TUYỂN DỤNG NỘI BỘ",
        WorkspacePersona.PersonalOwner => "WORKSPACE CỦA TÔI",
        _ => "CHƯA CẤP QUYỀN WORKSPACE"
    };

    public static IReadOnlyList<NavGroup> For(ClaimsPrincipal? user) => PersonaFor(user) switch
    {
        WorkspacePersona.SystemAdmin => Admin(),
        WorkspacePersona.InternalHr => Business(personal: false),
        WorkspacePersona.PersonalOwner => Business(personal: true),
        _ => []
    };

    private static IReadOnlyList<NavGroup> Admin() =>
    [
        new("Tổng quan hệ thống", [
            new("Dashboard quản trị", "admin/dashboard", DashboardIcon, NavLinkMatch.All, true),
            new("Báo cáo vận hành", "reports", ReportIcon, NavLinkMatch.All, true),
            new("Quyền truy cập", "permissions", ShieldIcon, NavLinkMatch.All)
        ]),
        new("Giám sát & tài nguyên", [
            new("Trạng thái hệ thống", "admin/system-health", StatusIcon, AdminOnly: true),
            new("Workspace & quota", "admin/workspaces", QuotaIcon, AdminOnly: true),
            new("Giám sát AI", "admin/ai-usage", AiIcon, AdminOnly: true),
            new("Kết nối Google & AI", "admin/integrations", PromptIcon, AdminOnly: true)
        ]),
        new("Bảo mật & vận hành", [
            new("Tài khoản", "users", UsersIcon, AdminOnly: true),
            new("Audit Log", "admin/activity-logs", AuditIcon, AdminOnly: true),
        ])
    ];

    private static IReadOnlyList<NavGroup> Business(bool personal) =>
    [
        new(personal ? "Không gian của tôi" : "Tổng quan tuyển dụng", [
            new(personal ? "Dashboard cá nhân" : "Dashboard tuyển dụng", "dashboard", DashboardIcon, NavLinkMatch.All),
            new(personal ? "Báo cáo & dữ liệu của tôi" : "Báo cáo tuyển dụng", personal ? "reports" : "reports/recruitment", ReportIcon, NavLinkMatch.All)
        ]),
        new("Quản lý tuyển dụng", [
            new("Vị trí tuyển dụng", "jobs", JobIcon),
            new("Tải CV lên", "upload", CvIcon),
            new("Ứng viên", "candidates", UsersIcon),
            new("Kết quả & xếp hạng", "results", ResultsIcon),
            new("Lịch sử lượt chấm", "runs", ClockIcon)
        ]),
        new("Công cụ AI", [
            new("System Prompt", "prompts", PromptIcon),
            new("Bộ dữ liệu đánh giá", "evaluation-sets", DataIcon)
        ]),
        new("Tài khoản & quyền", [
            new("Quyền truy cập", "permissions", ShieldIcon, NavLinkMatch.All)
        ])
    ];
}
