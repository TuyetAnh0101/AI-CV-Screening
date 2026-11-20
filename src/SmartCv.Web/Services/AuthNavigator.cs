using Microsoft.AspNetCore.Components;
using SmartCv.Web.Helpers;

namespace SmartCv.Web.Services;

/// <summary>
/// B28: chỗ DUY NHẤT đưa tab về trang đăng nhập (Scoped = một circuit). Trước B28, AuthGate tự chuyển hướng; nay
/// AuthorizeRouteView (RedirectToLogin), AuthGate (401 giữa chừng) và nút Đăng xuất cùng gọi vào đây, nên dù nhiều nơi
/// cùng phản ứng với một lần kết thúc phiên thì cũng chỉ có MỘT lần chuyển trang — không nhấp nháy, không vòng lặp.
///
/// - Chưa từng có phiên trong circuit (mở trang lần đầu, F5 khi token đã bị thu hồi): chuyển trong circuit (không tải lại).
/// - Phiên kết thúc giữa chừng (401, hết hạn, đăng xuất): tải lại hẳn trang đăng nhập (forceLoad) để circuit cũ và mọi
///   trạng thái Scoped (ô tìm kiếm, dữ liệu đã tải...) bị bỏ; thay mục lịch sử nên Back không quay lại trang cũ.
/// </summary>
public sealed class AuthNavigator(AuthSession session, NavigationManager nav)
{
    /// <summary>
    /// Đang tải lại trang (forceLoad): circuit này sắp bị bỏ, không chuyển trang / đăng xuất thêm lần nào nữa.
    /// B29: chỉ bật cho lần chuyển có tải lại. Lần chuyển TRONG circuit (mở trang lần đầu khi chưa đăng nhập) không bật cờ này —
    /// trước đây (B28) cờ bị giữ mãi nên sau khi đăng nhập trong cùng circuit, nút Đăng xuất / Đổi mật khẩu bị vô hiệu.
    /// </summary>
    public bool IsLeaving { get; private set; }

    /// <summary>Tab không còn đăng nhập → /login (kèm ?returnUrl an toàn, ?expired=1 hoặc ?signedOut=1).</summary>
    public void ToLogin()
    {
        if (IsLeaving) return;
        var forceLoad = session.HadSessionInCircuit;
        // Chuyển trong circuit: nếu đã ở /login rồi (một nơi khác vừa chuyển) thì không chuyển lần hai.
        if (!forceLoad && IsOnLogin()) return;
        if (forceLoad) IsLeaving = true;
        var url = session.EndReason == AuthEndReason.SignedOut
            ? AuthText.LoginPath + "?signedOut=1"
            : AuthText.LoginUrl("/" + nav.ToBaseRelativePath(nav.Uri), expired: session.EndReason == AuthEndReason.Expired);
        nav.NavigateTo(url, forceLoad: forceLoad, replace: true);
    }

    private bool IsOnLogin()
    {
        var path = nav.ToBaseRelativePath(nav.Uri).Split('?', '#')[0].TrimEnd('/');
        return path.Equals(AuthText.LoginPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Nút Đăng xuất (thanh trên cùng, menu di động, các màn hình chặn): xóa phiên rồi tải lại /login.</summary>
    public async Task SignOutAsync()
    {
        if (IsLeaving) return;
        IsLeaving = true;                   // trước await: RedirectToLogin vẽ lại vì Changed sẽ không chuyển trang lần hai
        await session.SignOutAsync();
        nav.NavigateTo(AuthText.LoginPath + "?signedOut=1", forceLoad: true, replace: true);
    }
}
