using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using SmartCv.Core.Auth;

namespace SmartCv.Web.Helpers;

/// <summary>Trạng thái truy cập của tab, suy ra từ ClaimsPrincipal do ApiAuthenticationStateProvider dựng.</summary>
public enum AccessLevel
{
    /// <summary>Chưa đăng nhập (hoặc phiên đã kết thúc) → trang đăng nhập.</summary>
    Anonymous,
    /// <summary>Có phiên đã lưu nhưng API chưa xác nhận được (mất kết nối lúc khôi phục) → chờ / thử lại, KHÔNG trao quyền.</summary>
    Unverified,
    /// <summary>Đã xác minh, tài khoản đang dùng mật khẩu tạm → A29/A30 chặn mọi API nghiệp vụ.</summary>
    PasswordChangeRequired,
    /// <summary>Đã xác minh nhưng vai trò không phải Admin/HR (dữ liệu lạ) → fail-closed, không vào trang nào.</summary>
    NoValidRole,
    Hr,
    Admin
}

/// <summary>Thao tác chỉ một số vai trò được làm. Mỗi giá trị ứng với đúng các API có chính sách riêng ở A29.</summary>
public enum AppPermission
{
    /// <summary>POST /api/jobs, PUT /api/jobs/{id}, POST /{id}/deactivate, /{id}/activate, cả nhóm /api/jobs/{id}/skills.</summary>
    ManageJobs,
    /// <summary>POST /api/prompts (lưu phiên bản mới), POST /api/prompts/{id}/activate.</summary>
    ManagePromptVersions,
    /// <summary>POST /api/prompts/assembled-preview (xem nội dung sẽ gửi Gemini).</summary>
    PreviewAssembledPrompt,
    /// <summary>POST /api/evaluation-sets (tạo bộ CV đánh giá). Thêm/bỏ hồ sơ, gán nhãn: HR cũng được.</summary>
    CreateEvaluationSet,
    /// <summary>B29: cả nhóm /api/users (xem, tạo, sửa, khóa / mở khóa, đặt lại mật khẩu HR).</summary>
    ManageUsers
}

/// <summary>
/// B28: một chỗ duy nhất quyết định "ai thấy / làm được gì" trên giao diện, bám đúng bảng quyền A29
/// (src/SmartCv.Api — AuthPolicies.Admin). Hàm thuần → unit test được. Đây chỉ là lớp HIỂN THỊ:
/// API (A29) luôn kiểm tra lại và là nơi quyết định cuối cùng.
/// Nguyên tắc fail-closed: thiếu thông tin, chưa xác minh, vai trò lạ → không có quyền.
/// </summary>
public static class AccessRules
{
    /// <summary>Claim riêng của Web: "verified" khi /api/auth/me hoặc lần đăng nhập đã xác nhận phiên trong circuit.</summary>
    public const string SessionClaim = "smartcv_session";
    public const string Verified = "verified";
    public const string Unverified = "unverified";
    public const string AuthenticationType = "SmartCv";

    /// <summary>Tên chính sách dùng trong [Authorize(Policy = ...)] của trang Blazor (đăng ký ở WebAuthSetup).</summary>
    public const string AdminPolicy = "SmartCv.Admin";
    /// <summary>Đã xác minh, vai trò hợp lệ, KỂ CẢ đang phải đổi mật khẩu (dành cho trang đổi mật khẩu của B29).</summary>
    public const string SignedInPolicy = "SmartCv.SignedIn";

    public static readonly ClaimsPrincipal Nobody = new(new ClaimsIdentity());

    public static AccessLevel Level(ClaimsPrincipal? user)
    {
        if (user?.Identity is not { IsAuthenticated: true } identity || identity.AuthenticationType != AuthenticationType) return AccessLevel.Anonymous;
        if (!user.HasClaim(SessionClaim, Verified)) return AccessLevel.Unverified;
        var admin = user.IsInRole(UserRoles.Admin);
        var hr = user.IsInRole(UserRoles.Hr);
        if (admin == hr) return AccessLevel.NoValidRole;   // không có vai trò, hoặc có cả hai (dữ liệu lạ)
        if (user.HasClaim(SmartCvClaims.MustChangePassword, "true")) return AccessLevel.PasswordChangeRequired;
        return admin ? AccessLevel.Admin : AccessLevel.Hr;
    }

    /// <summary>Được dùng các trang nghiệp vụ (giống chính sách mặc định của A29: Admin/HR, không phải đổi mật khẩu).</summary>
    public static bool CanUseApp(ClaimsPrincipal? user) => Level(user) is AccessLevel.Admin or AccessLevel.Hr;

    public static bool IsAdmin(ClaimsPrincipal? user) => Level(user) == AccessLevel.Admin;

    /// <summary>Bảng quyền thao tác. Hiện mọi thao tác có chính sách riêng ở A29 đều chỉ dành cho Admin.</summary>
    public static bool Can(ClaimsPrincipal? user, AppPermission permission) => permission switch
    {
        AppPermission.ManageJobs => IsAdmin(user),
        AppPermission.ManagePromptVersions => IsAdmin(user),
        AppPermission.PreviewAssembledPrompt => IsAdmin(user),
        AppPermission.CreateEvaluationSet => IsAdmin(user),
        AppPermission.ManageUsers => IsAdmin(user),
        _ => false
    };

    /// <summary>
    /// Người dùng hiện tại từ trạng thái cascade. Trạng thái chưa có / chưa xong → Nobody (fail-closed). Trong ứng dụng thật
    /// AuthorizeRouteView đã chờ xong trạng thái trước khi tạo trang, nên trang luôn nhận được bản đã hoàn tất.
    /// </summary>
    public static ClaimsPrincipal Current(Task<AuthenticationState>? state) =>
        state is { IsCompletedSuccessfully: true } done ? done.Result.User : Nobody;

    public static bool Can(Task<AuthenticationState>? state, AppPermission permission) => Can(Current(state), permission);

    public static async Task<ClaimsPrincipal> ResolveAsync(Task<AuthenticationState>? state)
    {
        if (state is null) return Nobody;
        try { return (await state).User; }
        catch (Exception error) when (error is not OutOfMemoryException) { return Nobody; }
    }

    /// <summary>Tên vai trò hiển thị cho người đang đăng nhập (không lộ claim kỹ thuật).</summary>
    public static string RoleName(ClaimsPrincipal? user) => Level(user) switch
    {
        AccessLevel.Admin => AuthText.RoleName(UserRoles.Admin),
        AccessLevel.Hr => AuthText.RoleName(UserRoles.Hr),
        AccessLevel.PasswordChangeRequired => AuthText.RoleName(user!.IsInRole(UserRoles.Admin) ? UserRoles.Admin : UserRoles.Hr),
        _ => AuthText.RoleName(null)
    };

    public static string? FullName(ClaimsPrincipal? user) => user?.FindFirst(SmartCvClaims.Name)?.Value;

    /// <summary>B29: mã tài khoản đang đăng nhập (claim "sub"); null nếu không có.</summary>
    public static int? UserId(ClaimsPrincipal? user) =>
        int.TryParse(user?.FindFirst(SmartCvClaims.UserId)?.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : null;
}
