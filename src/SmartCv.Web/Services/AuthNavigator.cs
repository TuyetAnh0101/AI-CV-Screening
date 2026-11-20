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
    public bool IsLeaving { get; private set; }

    /// <summary>Tab không còn đăng nhập → /login (kèm ?returnUrl an toàn, ?expired=1 hoặc ?signedOut=1).</summary>
    public void ToLogin()
    {
        if (IsLeaving) return;
        IsLeaving = true;
        var url = session.EndReason == AuthEndReason.SignedOut
            ? AuthText.LoginPath + "?signedOut=1"
            : AuthText.LoginUrl("/" + nav.ToBaseRelativePath(nav.Uri), expired: session.EndReason == AuthEndReason.Expired);
        nav.NavigateTo(url, forceLoad: session.HadSessionInCircuit, replace: true);
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
