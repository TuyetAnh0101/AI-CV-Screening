using SmartCv.Core.Auth;

namespace SmartCv.Web.Helpers;

/// <summary>B55: chỉ sinh URL từ API origin tin cậy; không dùng returnUrl hoặc startUrl làm external target tùy ý.</summary>
public static class ExternalAuthUiRules
{
    public static bool SupportedProvider(string? provider) => ExternalProviders.IsSupported(provider);

    public static string SafeDestination(string? proposed)
    {
        var safe = AuthText.SafeReturnUrl(proposed);
        // A58 dùng giới hạn 500 ký tự và chặn encoded slashes để tránh mở URL lạ sau khi decode.
        if (safe.Length > 500 || !safe.StartsWith('/') || safe.StartsWith("//", StringComparison.Ordinal)) return AuthText.DashboardPath;
        var decoded = safe;
        for (var i = 0; i < 3; i++)
        {
            try { decoded = Uri.UnescapeDataString(decoded); }
            catch (UriFormatException) { return AuthText.DashboardPath; }
            var path = decoded.Split('?', '#')[0];
            if (!decoded.StartsWith('/') || decoded.StartsWith("//", StringComparison.Ordinal) ||
                decoded.StartsWith("/\\", StringComparison.Ordinal) || decoded.Contains('\\') ||
                path.Contains(':') || decoded.Any(char.IsControl)) return AuthText.DashboardPath;
        }
        return safe;
    }

    public static string ApiRoot(string? configured)
    {
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query))
            throw new InvalidOperationException("ApiBaseUrl không hợp lệ. Cần HTTPS API origin.");
        // Không cho dự án ghép OAuth URL vào đường dẫn bên dưới site khác.
        return uri.GetLeftPart(UriPartial.Authority) + "/";
    }

    public static string LoginStartUrl(string? apiBase, string provider, string? returnUrl)
    {
        if (!SupportedProvider(provider)) throw new ArgumentException("Provider không hỗ trợ.", nameof(provider));
        var relative = $"api/auth/external/{provider}/start?intent=login&returnUrl=" +
            Uri.EscapeDataString(SafeDestination(returnUrl));
        return new Uri(new Uri(ApiRoot(apiBase)), relative).AbsoluteUri;
    }

    /// <summary>A58 trả /api/auth/external/google/start?intent=link&ticket=...; chỉ cho phép đường dẫn này.</summary>
    public static string LinkStartUrl(string? apiBase, string provider, string? startUrl)
    {
        if (!SupportedProvider(provider) || string.IsNullOrWhiteSpace(startUrl) ||
            !startUrl.StartsWith($"/api/auth/external/{provider}/start?intent=link&ticket=", StringComparison.Ordinal) ||
            startUrl.Contains('#') || startUrl.Contains('\\') || startUrl.Any(char.IsControl))
            throw new InvalidOperationException("API trả về địa chỉ liên kết không an toàn.");
        var target = new Uri(new Uri(ApiRoot(apiBase)), startUrl);
        if (target.GetLeftPart(UriPartial.Authority) != new Uri(ApiRoot(apiBase)).GetLeftPart(UriPartial.Authority))
            throw new InvalidOperationException("Địa chỉ liên kết không thuộc SCApp API.");
        return target.AbsoluteUri;
    }

    public static bool IsValidCode(string? code) => code is { Length: 43 } &&
        code.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    public static string? ExchangeError(string? code) => code switch
    {
        "token_expired" => "Mã đăng nhập đã hết hạn. Hãy thử đăng nhập Google một lần nữa.",
        "token_invalid" => "Mã đăng nhập không hợp lệ hoặc đã được sử dụng. Vui lòng bắt đầu lại.",
        "rate_limited" or "external_rate_limited" or "public_auth_rate_limited" => "Bạn thao tác quá nhanh. Hãy đợi một lúc rồi thử lại.",
        _ => null
    };
}
