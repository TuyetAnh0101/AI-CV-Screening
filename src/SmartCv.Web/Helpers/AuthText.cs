using SmartCv.Core.Auth;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

/// <summary>B27: địa chỉ trang đăng nhập, chống chuyển hướng ra ngoài và câu thông báo lỗi. Hàm thuần → unit test được.</summary>
public static class AuthText
{
    public const string LoginPath = "login";

    /// <summary>
    /// Chỉ nhận đường dẫn TRONG ứng dụng ("/cvs?search=java", "cvs"). Địa chỉ ngoài ("https://lua-dao.vn", "//lua-dao.vn",
    /// "/\lua-dao.vn") → "/" — tránh kẻ xấu gửi link đăng nhập rồi chuyển người dùng sang trang giả. "/login" → "/" (không vòng lặp).
    /// </summary>
    public static string SafeReturnUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "/";
        var u = url.Trim();
        var rawPath = u.Split('?', '#')[0];
        // "://", "//", "\": địa chỉ ngoài; ":" trong phần đường dẫn: scheme như "javascript:" hay "mailto:"; ký tự điều khiển: chèn header.
        if (u.Length > 2048 || u.StartsWith("//") || u.StartsWith("/\\") || u.StartsWith('\\') || rawPath.Contains(':')
            || rawPath.Contains('\\') || u.Any(char.IsControl))
            return "/";
        if (!u.StartsWith('/')) u = "/" + u;
        var path = u.Split('?', '#')[0].TrimEnd('/');
        if (path.Equals("/login", StringComparison.OrdinalIgnoreCase)) return "/";
        return u;
    }

    /// <summary>"login?returnUrl=%2Fcvs%3Fsearch%3Djava" (+ "&amp;expired=1" khi phiên hết hạn giữa chừng).</summary>
    public static string LoginUrl(string? currentRelativeUrl, bool expired = false)
    {
        var ret = SafeReturnUrl(currentRelativeUrl);
        var q = new List<string>();
        if (ret != "/") q.Add("returnUrl=" + Uri.EscapeDataString(ret));
        if (expired) q.Add("expired=1");
        return LoginPath + (q.Count == 0 ? "" : "?" + string.Join("&", q));
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
    public static string LoginError<T>(ApiResult<T> result) => result.FailureKind switch
    {
        ApiFailureKind.Network => "Không kết nối được máy chủ SCApp. Kiểm tra kết nối mạng hoặc thử lại sau ít phút.",
        ApiFailureKind.Timeout => "Máy chủ phản hồi quá lâu. Vui lòng thử lại.",
        ApiFailureKind.InvalidData => "Máy chủ trả về dữ liệu không hợp lệ. Vui lòng thử lại sau.",
        _ => (result.StatusCode, result.ErrorCode) switch
        {
            (400, _) => "Thông tin đăng nhập chưa hợp lệ. Kiểm tra lại email và mật khẩu.",
            (401, _) => InvalidCredentials,
            (403, "account_locked") => "Tài khoản này đã bị khóa. Liên hệ quản trị viên (Admin) nếu cần mở lại.",
            (403, _) => "Tài khoản này hiện không được phép đăng nhập. Liên hệ quản trị viên (Admin).",
            (429, _) => "Bạn đã thử đăng nhập quá nhiều lần. Vui lòng đợi rồi thử lại.",
            (503, _) => "Dịch vụ đăng nhập tạm thời không khả dụng. Vui lòng thử lại sau ít phút.",
            (>= 500, _) => "Máy chủ gặp sự cố khi đăng nhập. Vui lòng thử lại sau.",
            _ => "Không đăng nhập được. Vui lòng thử lại."
        }
    };
}
