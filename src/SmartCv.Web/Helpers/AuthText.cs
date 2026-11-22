using SmartCv.Core.Auth;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

/// <summary>B27: địa chỉ trang đăng nhập, chống chuyển hướng ra ngoài và câu thông báo lỗi. Hàm thuần → unit test được.</summary>
public static class AuthText
{
    public const string LoginPath = "login";
    public const string ChangePasswordPath = "change-password";
    /// <summary>B51: đích mặc định sau đăng nhập / đổi mật khẩu (Landing "/" không phải đích).</summary>
    public const string DashboardPath = "/dashboard";

    /// <summary>
    /// Chỉ nhận đường dẫn TRONG ứng dụng ("/cvs?search=java", "cvs"). Địa chỉ ngoài ("https://lua-dao.vn", "//lua-dao.vn",
    /// "/\lua-dao.vn") → "/dashboard" — tránh kẻ xấu gửi link đăng nhập rồi chuyển người dùng sang trang giả.
    /// B51: "/" (Landing) và "/login" cũng → "/dashboard" (không vòng lặp, đăng nhập xong vào thẳng Dashboard).
    /// </summary>
    public static string SafeReturnUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return DashboardPath;
        var u = url.Trim();
        var rawPath = u.Split('?', '#')[0];
        // "://", "//", "\": địa chỉ ngoài; ":" trong phần đường dẫn: scheme như "javascript:" hay "mailto:"; ký tự điều khiển: chèn header.
        if (u.Length > 2048 || u.StartsWith("//", StringComparison.Ordinal) || u.StartsWith("/\\", StringComparison.Ordinal)
            || u.StartsWith('\\') || rawPath.Contains(':') || rawPath.Contains('\\') || u.Any(char.IsControl))
            return DashboardPath;
        if (!u.StartsWith('/')) u = "/" + u;
        var path = u.Split('?', '#')[0].TrimEnd('/');
        if (path.Length == 0 || path.Equals("/login", StringComparison.OrdinalIgnoreCase)) return DashboardPath;
        return u;
    }

    private static bool IsExplicitDashboard(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var path = url.Trim().Split('?', '#')[0].Trim('/');
        return path.Equals("dashboard", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "login?returnUrl=%2Fcvs%3Fsearch%3Djava" (+ "&amp;expired=1" khi phiên hết hạn giữa chừng).
    /// Người xin mở /dashboard PHẢI giữ returnUrl=%2Fdashboard; Landing "/" hoặc URL không hợp lệ thì không gắn returnUrl.
    /// </summary>
    public static string LoginUrl(string? currentRelativeUrl, bool expired = false)
    {
        var ret = SafeReturnUrl(currentRelativeUrl);
        var q = new List<string>();
        if (ret != DashboardPath || IsExplicitDashboard(currentRelativeUrl)) q.Add("returnUrl=" + Uri.EscapeDataString(ret));
        if (expired) q.Add("expired=1");
        return LoginPath + (q.Count == 0 ? "" : "?" + string.Join("&", q));
    }

    /// <summary>
    /// B29: "change-password?returnUrl=%2Fcvs" — trang đổi mật khẩu nhớ trang đang mở (chỉ đường dẫn trong ứng dụng).
    /// Không quay về chính trang đổi mật khẩu hay /login.
    /// </summary>
    public static string ChangePasswordUrl(string? currentRelativeUrl)
    {
        var ret = AfterPasswordChange(currentRelativeUrl);
        return ChangePasswordPath + (ret == DashboardPath && !IsExplicitDashboard(currentRelativeUrl) ? "" : "?returnUrl=" + Uri.EscapeDataString(ret));
    }

    /// <summary>B29: trang quay về sau khi đổi mật khẩu xong — an toàn, không phải /change-password.</summary>
    public static string AfterPasswordChange(string? returnUrl)
    {
        var ret = SafeReturnUrl(returnUrl);
        var path = ret.Split('?', '#')[0].TrimEnd('/');
        return path.Equals("/" + ChangePasswordPath, StringComparison.OrdinalIgnoreCase) ? DashboardPath : ret;
    }

    /// <summary>Tên vai trò hiển thị.</summary>
    public static string RoleName(string? role) => role switch
    {
        UserRoles.Admin => "Quản trị viên",
        UserRoles.Hr => "Nhân sự (HR)",
        _ => "Người dùng"
    };

    /// <summary>"Nguyễn Thị Lan" → "NL"; tên trống → "?".</summary>
    public static string Initials(string? fullName)
    {
        var parts = (fullName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant()
        };
    }

    public const string InvalidCredentials = "Email hoặc mật khẩu không đúng.";
    public const string SessionExpired = "Phiên đăng nhập đã hết hạn hoặc không còn hiệu lực. Vui lòng đăng nhập lại.";
    public const string SignedOut = "Bạn đã đăng xuất khỏi SCApp.";

    /// <summary>
    /// Câu thông báo cho một lần đăng nhập thất bại, dựa vào mã trạng thái / mã lỗi (code) của A28 và loại lỗi phía Web —
    /// không dựa vào câu chữ của API. Sai email và sai mật khẩu luôn cùng một câu (không lộ email có tồn tại hay không).
    /// </summary>
    /// <summary>B55: A58/A59 provider errors; no email, token or raw server detail is exposed.</summary>
    public static string ExternalLoginError(string? code) => code switch
    {
        "cancelled" => "Bạn đã hủy đăng nhập bằng tài khoản ngoài. Bạn có thể thử lại hoặc dùng mật khẩu.",
        "provider_error" => "Không thể kết nối với nhà cung cấp đăng nhập. Vui lòng thử lại.",
        "email_unverified" => "Email tại nhà cung cấp chưa được xác minh. Hãy xác minh email trước khi đăng nhập.",
        "link_required" => "Email này đã có tài khoản SCApp. Hãy đăng nhập bằng mật khẩu, sau đó vào Tài khoản → Liên kết Google.",
        "account_locked" => "Tài khoản SCApp đã bị khóa. Vui lòng liên hệ quản trị viên.",
        "admin_not_allowed" => "Tài khoản Admin chỉ được đăng nhập bằng mật khẩu SCApp.",
        "registration_disabled" => "Đăng ký tài khoản mới đang tắt. Hãy dùng một tài khoản đã được liên kết hoặc liên hệ quản trị viên.",
        "already_linked" => "Tài khoản ngoài này đã liên kết với một tài khoản SCApp khác.",
        "token_invalid" => "Phiên liên kết hoặc mã đăng nhập không hợp lệ. Vui lòng thử lại.",
        "token_expired" => "Liên kết đã hết hạn. Vui lòng bắt đầu lại.",
        "last_login_method" => "Không thể hủy phương thức đăng nhập cuối cùng. Hãy thiết lập mật khẩu trước.",
        _ => "Không thể đăng nhập bằng nhà cung cấp ngoài. Vui lòng thử lại."
    };

    public static string LoginError<T>(ApiResult<T> result) => result.FailureKind switch
    {
        ApiFailureKind.Network => "Không kết nối được máy chủ SCApp. Kiểm tra kết nối mạng hoặc thử lại sau ít phút.",
        ApiFailureKind.Timeout => "Máy chủ phản hồi quá lâu. Vui lòng thử lại.",
        ApiFailureKind.InvalidData => "Máy chủ trả về dữ liệu không hợp lệ. Vui lòng thử lại sau.",
        _ => (result.StatusCode, result.ErrorCode) switch
        {
            (400, _) => "Thông tin đăng nhập chưa hợp lệ. Kiểm tra lại email và mật khẩu.",
            (401, _) => InvalidCredentials,
            (403, "email_not_verified") => "Tài khoản chưa xác minh email. Hãy mở liên kết trong hộp thư hoặc yêu cầu gửi lại.",
            (403, "account_locked") => "Tài khoản này đã bị khóa. Liên hệ quản trị viên (Admin) nếu cần mở lại.",
            (403, _) => "Tài khoản này hiện không được phép đăng nhập. Liên hệ quản trị viên (Admin).",
            (429, _) => "Bạn đã thử đăng nhập quá nhiều lần. Vui lòng đợi rồi thử lại.",
            (503, _) => "Dịch vụ đăng nhập tạm thời không khả dụng. Vui lòng thử lại sau ít phút.",
            (>= 500, _) => "Máy chủ gặp sự cố khi đăng nhập. Vui lòng thử lại sau.",
            _ => "Không đăng nhập được. Vui lòng thử lại."
        }
    };
}
