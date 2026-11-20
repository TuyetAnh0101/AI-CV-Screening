namespace SmartCv.Web.Services;

/// <summary>
/// B29: thông báo dùng MỘT lần trong circuit, cho trang đổi mật khẩu. Đổi xong, tài khoản dùng mật khẩu tạm chuyển từ khung trống
/// sang khung đầy đủ (AuthGate) nên trang được tạo lại; thông báo này giúp trang mới vẫn hiện "Đã đổi mật khẩu" thay vì form trống.
/// Không chứa mật khẩu hay token. Scoped = một tab.
/// </summary>
public sealed class AccountNotice
{
    private string? _passwordChangedReturnUrl;

    public void PasswordChanged(string returnUrl) => _passwordChangedReturnUrl = returnUrl;

    /// <summary>Lấy và xóa: chỉ trang đầu tiên đọc được.</summary>
    public string? TakePasswordChanged()
    {
        var value = _passwordChangedReturnUrl;
        _passwordChangedReturnUrl = null;
        return value;
    }
}
