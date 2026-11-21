using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace SmartCv.Web.Services;

/// <summary>B52: cấu hình công khai. Chỉ dùng HttpClient KHÔNG Bearer, cache trong circuit.</summary>
public sealed class PublicSettingsClient(HttpClient http)
{
    private readonly object _sync = new();
    private Task<bool>? _cached;

    public Task<bool> IsRegistrationEnabledAsync()
    {
        lock (_sync) return _cached ??= ReadAsync();
    }

    private async Task<bool> ReadAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/auth/registration-settings");
            // Không gắn AuthSession, token hay bất kỳ thông tin định danh nào.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return false; // 404 (chưa có A56), 503, 429...
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("registrationEnabled", out var enabled)
                && enabled.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or OperationCanceledException or JsonException or InvalidOperationException)
        {
            // Thất bại an toàn: không hiển thị CTA đăng ký.
            return false;
        }
    }
}

public enum PublicCtaKind { Dashboard, ChangePassword, Register, SignIn }

public sealed record PublicCta(PublicCtaKind Kind, string Text, string Href, string? Note = null)
{
    public static PublicCta SignIn { get; } = new(
        PublicCtaKind.SignIn,
        "Đăng nhập",
        "/login",
        "Đăng ký công khai sắp mở.");
        public static PublicCta Resolve(bool canUseApp, bool mustChangePassword,
        bool registrationEnabled, bool registerRouteAvailable, bool isAdmin = false)
    {
        if (canUseApp)
            return new(PublicCtaKind.Dashboard, "Vào bảng điều khiển",
                isAdmin ? "/admin/dashboard" : "/dashboard");
        if (mustChangePassword)
            return new(PublicCtaKind.ChangePassword, "Hoàn tất đổi mật khẩu", "/change-password");
        if (registrationEnabled && registerRouteAvailable)
            return new(PublicCtaKind.Register, "Bắt đầu miễn phí", "/register");
        return SignIn;
    }

    // Kiểm tra route thật tại runtime, không hứa nút đăng ký khi B53 chưa triển khai.
    public static bool RegisterRouteAvailable { get; } =
        typeof(SmartCv.Web.Components.Pages.Landing).Assembly.GetTypes().Any(type =>
            type.GetCustomAttributes(typeof(RouteAttribute), inherit: true)
                .OfType<RouteAttribute>()
                .Any(route => route.Template.TrimEnd('/').Equals("/register", StringComparison.OrdinalIgnoreCase)));
}