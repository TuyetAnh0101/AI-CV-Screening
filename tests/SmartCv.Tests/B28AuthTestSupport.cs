extern alias web;

using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Services;
namespace SmartCv.Tests;

/// <summary>B28: trạng thái đăng nhập cố định cho test giao diện (HtmlRenderer / Renderer tự viết). Không có token, không gọi API.</summary>
internal sealed class B28FixedAuthState(ClaimsPrincipal user) : AuthenticationStateProvider
{
    private AuthenticationState _state = new(user);
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(_state);

    public void Set(ClaimsPrincipal next)
    {
        _state = new AuthenticationState(next);
        NotifyAuthenticationStateChanged(Task.FromResult(_state));
    }
}

/// <summary>
/// B28: một "tab" dùng DI thật của Web (AddSmartCvApiClients) với máy chủ API giả. Dùng để kết thúc phiên đúng đường thật:
/// API trả 401 / 403 password_change_required cho một request nghiệp vụ (ApiClientBase → AuthSession).
/// </summary>
internal sealed class B28Tab : IAsyncDisposable
{
    public required ServiceProvider Web { get; init; }
    public required AsyncServiceScope Scope { get; init; }
    public required B27CaptureHandler Api { get; init; }
    public AuthSession Session => Scope.ServiceProvider.GetRequiredService<AuthSession>();
    public T Get<T>() where T : notnull => Scope.ServiceProvider.GetRequiredService<T>();

    public static async Task<B28Tab> SignedInAsync(string role = "HR", string token = "tok")
    {
        var api = new B27CaptureHandler();
        var web = B27.WebServices(api);
        var tab = new B28Tab { Web = web, Scope = web.CreateAsyncScope(), Api = api };
        await tab.Session.SignInAsync(B27.Login(token, role));
        return tab;
    }

    /// <summary>API trả 401 cho request kế tiếp (token bị thu hồi / hết hạn phía máy chủ).</summary>
    public Task<ApiResult<List<JobDto>>> RevokeAsync()
    {
        Api.Respond = _ => Task.FromResult(B27CaptureHandler.Problem(System.Net.HttpStatusCode.Unauthorized, "authentication_required"));
        return Get<JobApiClient>().GetJobsAsync();
    }

    /// <summary>API trả 403 password_change_required (A30: Admin đặt lại mật khẩu).</summary>
    public Task<ApiResult<List<JobDto>>> RequirePasswordChangeAsync()
    {
        Api.Respond = _ => Task.FromResult(B27CaptureHandler.Problem(System.Net.HttpStatusCode.Forbidden, "password_change_required"));
        return Get<JobApiClient>().GetJobsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Scope.DisposeAsync();
        await Web.DisposeAsync();
    }
}

internal static class B28Auth
{
    public static MeDto User(string role = "Admin", bool mustChange = false, string name = "Nguyễn Thị Lan", int id = 7) =>
        new() { UserId = id, Email = "lan.hr@test.local", FullName = name, Role = role, MustChangePassword = mustChange };

    /// <summary>Principal đúng như ApiAuthenticationStateProvider dựng cho một phiên.</summary>
    public static ClaimsPrincipal Principal(string role = "Admin", bool verified = true, bool mustChange = false) =>
        ApiAuthenticationStateProvider.ToPrincipal(User(role, mustChange), verified);

    /// <summary>
    /// Đăng ký trạng thái đăng nhập cascade cho component (giống AddSmartCvWebAuth của Web, nhưng trạng thái cố định).
    /// Mặc định Admin để các test giao diện tuần trước (viết khi chưa phân quyền) vẫn thấy đủ thao tác như cũ.
    /// </summary>
    public static IServiceCollection AddB28Auth(this IServiceCollection services, string role = "Admin") =>
        services.AddB28Auth(Principal(role));

    public static IServiceCollection AddB28Auth(this IServiceCollection services, ClaimsPrincipal user)
    {
        services.AddCascadingAuthenticationState();
        services.AddSingleton<AuthenticationStateProvider>(new B28FixedAuthState(user));
        // NavMenu / UserMenu dùng AuthNavigator cho nút Đăng xuất (chỉ được tạo khi component đó được vẽ).
        services.TryAddSingleton(_ => B27.Session());
        services.TryAddScoped<AuthNavigator>();
        return services;
    }
}
