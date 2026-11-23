using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;
using SmartCv.Web.Helpers;

namespace SmartCv.Web.Services;

/// <summary>
/// B28: cho Blazor biết ai đang đăng nhập và với vai trò gì (AuthorizeRouteView, [Authorize(Policy = ...)], quyền thao tác).
///
/// Chỉ là lớp ĐỌC trên <see cref="AuthSession"/> của B27 (Scoped = một circuit): không lưu token, không đọc storage, không
/// gọi API riêng. Vai trò lấy từ người dùng mà AuthSession đã xác nhận — bằng /api/auth/me lúc khôi phục phiên (F5, tab mới
/// cùng phiên) hoặc bằng chính phản hồi đăng nhập — không bao giờ từ dữ liệu client tự khai.
///
/// Fail-closed:
///  - chưa khôi phục xong → Task chưa hoàn tất (AuthorizeRouteView hiện "Authorizing", không tạo trang);
///  - phiên chưa xác minh được (API mất kết nối) → danh tính có cờ "unverified", KHÔNG có claim vai trò;
///  - vai trò không phải Admin/HR → không có claim vai trò.
/// AuthSession.Changed (đăng nhập, đăng xuất, 401, đổi mật khẩu bắt buộc, xác minh lại) → NotifyAuthenticationStateChanged.
/// </summary>
public sealed class ApiAuthenticationStateProvider : AuthenticationStateProvider, IDisposable
{
    private static readonly AuthenticationState Anonymous = new(AccessRules.Nobody);

    private readonly AuthSession _session;
    private Task<AuthenticationState>? _initial;
    private bool _disposed;

    public ApiAuthenticationStateProvider(AuthSession session)
    {
        _session = session;
        _session.Changed += OnSessionChanged;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
        _session.IsRestored ? Task.FromResult(Build(_session)) : _initial ??= RestoreThenBuildAsync();

    private async Task<AuthenticationState> RestoreThenBuildAsync()
    {
        await _session.EnsureRestoredAsync();   // không bao giờ ném lỗi (B27)
        return Build(_session);
    }

    private void OnSessionChanged()
    {
        if (_disposed) return;
        NotifyAuthenticationStateChanged(Task.FromResult(Build(_session)));
    }

    /// <summary>Trạng thái hiện tại của phiên → AuthenticationState. Không có token trong claim.</summary>
    public static AuthenticationState Build(AuthSession session) =>
        session.IsSignedIn && session.User is { } user ? new AuthenticationState(ToPrincipal(user, session.IsVerified)) : Anonymous;

    /// <summary>
    /// Người dùng → ClaimsPrincipal, cùng tên claim với API (SmartCvClaims: "sub", "name", "email", "role") và khai báo
    /// roleType = "role" để IsInRole / RequireRole hoạt động. Vai trò chỉ được thêm khi phiên đã xác minh và hợp lệ.
    /// </summary>
    public static ClaimsPrincipal ToPrincipal(MeDto user, bool verified)
    {
        var claims = new List<Claim>
        {
            new(SmartCvClaims.UserId, user.UserId.ToString(CultureInfo.InvariantCulture)),
            new(SmartCvClaims.Name, user.FullName ?? ""),
            new(SmartCvClaims.Email, user.Email ?? ""),
            new(AccessRules.SessionClaim, verified ? AccessRules.Verified : AccessRules.Unverified),
        };
        if (verified && user.Workspace is { Id: > 0 } workspace)
        {
            claims.Add(new Claim(AccessRules.WorkspaceKindClaim, workspace.Kind));
            claims.Add(new Claim(AccessRules.WorkspaceRoleClaim, workspace.Role));
        }
        if (verified && UserRoles.IsValid(user.Role))
        {
            claims.Add(new Claim(SmartCvClaims.Role, user.Role));
            claims.Add(new Claim(SmartCvClaims.MustChangePassword, user.MustChangePassword ? "true" : "false"));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AccessRules.AuthenticationType, SmartCvClaims.Name, SmartCvClaims.Role));
    }

    public void Dispose()
    {
        _disposed = true;
        _session.Changed -= OnSessionChanged;
    }
}
