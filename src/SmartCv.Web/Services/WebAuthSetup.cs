using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Components.Authorization;
using SmartCv.Core.Auth;
using SmartCv.Web.Helpers;

namespace SmartCv.Web.Services;

/// <summary>
/// B28: phân quyền trang Blazor.
///
/// - Chính sách mặc định ([Authorize] của Components/Pages/_Imports.razor) = chính sách nghiệp vụ của A29: đã xác minh,
///   vai trò Admin hoặc HR, không phải đổi mật khẩu. <see cref="AccessRules.AdminPolicy"/> thêm vai trò Admin.
/// - Bước tải trang HTTP: token nằm trong circuit Blazor (AuthSession), không nằm trong cookie, nên ở bước đó ai cũng là
///   "chưa đăng nhập". Bước HTTP CHO QUA (<see cref="PassThroughAuthorizationResultHandler"/>): prerender đã tắt (App.razor) nên
///   máy chủ chỉ trả khung HTML trống, không có dữ liệu. Kiểm tra thật do AuthorizeRouteView (Routes.razor) làm trong circuit,
///   và mọi dữ liệu luôn đi qua API có kiểm tra token (A29).
/// Program.cs: app.UseSmartCvWebAuth(); NGAY TRƯỚC app.UseAntiforgery();
/// </summary>
public static class WebAuthSetup
{
    public static IServiceCollection AddSmartCvWebAuth(this IServiceCollection services)
    {
        services.AddAuthorization(o =>
        {
            var business = BusinessPolicy();
            o.DefaultPolicy = business;
            o.AddPolicy(AccessRules.AdminPolicy, new AuthorizationPolicyBuilder(business).RequireRole(UserRoles.Admin).Build());
            o.AddPolicy(AccessRules.SignedInPolicy, p => p.RequireAuthenticatedUser()
                .RequireClaim(AccessRules.SessionClaim, AccessRules.Verified).RequireRole(UserRoles.Admin, UserRoles.Hr));
        });
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, PassThroughAuthorizationResultHandler>();

        // Một AuthenticationStateProvider cho mỗi circuit, bọc AuthSession của chính circuit đó.
        services.AddCascadingAuthenticationState();
        services.AddScoped<ApiAuthenticationStateProvider>();
        services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<ApiAuthenticationStateProvider>());
        services.AddScoped<AuthNavigator>();
        return services;
    }

    /// <summary>Giống chính sách mặc định của A29 (AuthSetup.cs của API), cộng điều kiện "phiên đã xác minh" của Web.</summary>
    public static AuthorizationPolicy BusinessPolicy() => new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim(AccessRules.SessionClaim, AccessRules.Verified)
        .RequireRole(UserRoles.Admin, UserRoles.Hr)
        .RequireAssertion(c => AccessRules.CanUseApp(c.User))
        .Build();

    public static WebApplication UseSmartCvWebAuth(this WebApplication app)
    {
        app.UseAuthorization();
        return app;
    }
}

/// <summary>Bước HTTP luôn cho qua — xem giải thích ở <see cref="WebAuthSetup"/>.</summary>
public sealed class PassThroughAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult) =>
        next(context);
}
