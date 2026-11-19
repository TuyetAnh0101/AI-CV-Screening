using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.JSInterop;

namespace SmartCv.Web.Services;

/// <summary>
/// B27: lưu phiên vào sessionStorage của tab, mã hóa bằng Data Protection của máy chủ Web (ProtectedSessionStorage).
/// - Mỗi tab một phiên; đóng tab là hết. Muốn nhớ qua các tab thì đổi sang ProtectedLocalStorage (kém an toàn trên máy dùng chung).
/// - Khóa Data Protection đổi (máy chủ Web cài lại) → không giải mã được → coi như chưa đăng nhập.
/// - Chỉ chạy được khi circuit đã có JavaScript; App.razor tắt prerender nên luôn thỏa.
/// </summary>
public sealed class ProtectedSessionTokenStore(ProtectedSessionStorage storage, ILogger<ProtectedSessionTokenStore> logger) : ITokenStore
{
    internal const string Key = "smartcv.auth";

    public async Task<AuthSnapshot?> LoadAsync()
    {
        try
        {
            var result = await storage.GetAsync<AuthSnapshot>(Key);
            return result.Success ? result.Value : null;
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            logger.LogInformation("Phiên đăng nhập đã lưu không đọc được (khóa Data Protection đã đổi hoặc dữ liệu hỏng); cần đăng nhập lại.");
            await ClearAsync();
            return null;
        }
        catch (Exception error) when (IsJsUnavailable(error))
        {
            return null;
        }
    }

    public async Task SaveAsync(AuthSnapshot snapshot)
    {
        try { await storage.SetAsync(Key, snapshot); }
        catch (Exception error) when (IsJsUnavailable(error)) { /* vẫn nhớ trong bộ nhớ của circuit hiện tại */ }
    }

    public async Task ClearAsync()
    {
        try { await storage.DeleteAsync(Key); }
        catch (Exception error) when (IsJsUnavailable(error)) { }
    }

    /// <summary>Đang prerender (chưa có JS), trình duyệt vừa ngắt kết nối, hoặc lời gọi JS bị hủy.</summary>
    private static bool IsJsUnavailable(Exception error) =>
        error is InvalidOperationException or JSDisconnectedException or TaskCanceledException or JSException;
}
