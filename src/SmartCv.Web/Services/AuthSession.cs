using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

/// <summary>Những gì nhớ sau khi đăng nhập (lưu mã hóa trong ProtectedSessionStorage của tab). Không bao giờ có mật khẩu.</summary>
public sealed record AuthSnapshot(string Token, DateTime ExpiresAt, MeDto User);

/// <summary>Nơi lưu phiên đăng nhập. Tách interface để test thay bằng bản lưu trong bộ nhớ.</summary>
public interface ITokenStore
{
    Task<AuthSnapshot?> LoadAsync();
    Task SaveAsync(AuthSnapshot snapshot);
    Task ClearAsync();
}

/// <summary>Vì sao tab hiện không còn đăng nhập (để trang /login hiện đúng câu).</summary>
public enum AuthEndReason
{
    None,
    /// <summary>Người dùng bấm Đăng xuất.</summary>
    SignedOut,
    /// <summary>Token hết hạn, bị thu hồi (khóa tài khoản, đổi SecurityStamp) hoặc API trả 401.</summary>
    Expired
}

/// <summary>
/// B27: phiên đăng nhập của MỘT tab trình duyệt. Đăng ký Scoped, nên trong Blazor Interactive Server mỗi circuit có một
/// bản riêng — token của người A không bao giờ nằm trong bản của người B. Không static, không singleton.
///
/// Token chỉ nằm trong bộ nhớ máy chủ Web và trong sessionStorage của tab ở dạng đã mã hóa bằng Data Protection
/// (ProtectedSessionStorage) — JavaScript trên trang không đọc được. Đóng tab là phải đăng nhập lại.
///
/// B28 sẽ bọc lớp này bằng AuthenticationStateProvider (vai trò, AuthorizeView); không tạo cơ chế thứ hai song song.
/// </summary>
public sealed class AuthSession(ITokenStore store, IAuthApi authApi, TimeProvider time, ILogger<AuthSession> logger)
{
    private Task? _restore;

    /// <summary>Token hiện tại (kể cả đã quá hạn). Chỉ dùng nội bộ trong SmartCv.Web — không hiện, không ghi log.</summary>
    internal string? Token { get; private set; }
    public DateTime? ExpiresAt { get; private set; }
    public MeDto? User { get; private set; }

    /// <summary>true khi /api/auth/me (hoặc chính lần đăng nhập) đã xác nhận token trong circuit này.</summary>
    public bool IsVerified { get; private set; }

    public AuthEndReason EndReason { get; private set; }

    public bool IsRestored => _restore is { IsCompleted: true };
    public bool IsSignedIn => Token is not null && ExpiresAt is { } e && e > time.GetUtcNow().UtcDateTime;
    public bool IsAdmin => IsSignedIn && User?.Role == UserRoles.Admin;
    public bool MustChangePassword => IsSignedIn && User?.MustChangePassword == true;

    /// <summary>Đăng nhập / đăng xuất / đổi thông tin người dùng.</summary>
    public event Action? Changed;

    /// <summary>Phiên kết thúc ngoài ý muốn (API trả 401, token quá hạn) → giao diện đưa về /login?expired=1. Báo đúng một lần.</summary>
    public event Action? Expired;

    /// <summary>
    /// Đọc phiên đã lưu và xác minh lại với API. Gọi nhiều chỗ (AuthGate, trang Login, mọi API client) cũng chỉ chạy một lần
    /// cho cả tab. Không bao giờ ném lỗi: lỗi storage / mạng → coi như chưa đăng nhập hoặc giữ phiên chưa xác minh.
    /// </summary>
    public Task EnsureRestoredAsync() => _restore ??= RestoreAsync();

    private async Task RestoreAsync()
    {
        AuthSnapshot? saved;
        try { saved = await store.LoadAsync(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("Không đọc được phiên đăng nhập đã lưu ({Error}); coi như chưa đăng nhập.", error.GetType().Name);
            return;
        }

        if (saved is null || string.IsNullOrWhiteSpace(saved.Token)) return;
        if (saved.ExpiresAt <= time.GetUtcNow().UtcDateTime)
        {
            EndReason = AuthEndReason.Expired;
            await SafeClearAsync();
            return;
        }

        // Không tin token trong storage là còn hiệu lực: tài khoản có thể đã bị khóa hoặc đổi quyền.
        var me = await authApi.GetMeAsync(saved.Token);
        if (me.IsSuccess && me.Data is { } user)
        {
            (Token, ExpiresAt, User, IsVerified) = (saved.Token, saved.ExpiresAt, user, true);
            await SafeSaveAsync();
            return;
        }

        if (me.StatusCode == 401)
        {
            EndReason = AuthEndReason.Expired;
            await SafeClearAsync();
            return;
        }

        // API tạm không trả lời (mất kết nối, 503, quá thời gian): giữ phiên chưa xác minh. Mỗi request sau vẫn được API
        // kiểm tra token; 401 lúc đó sẽ đưa về trang đăng nhập.
        (Token, ExpiresAt, User, IsVerified) = (saved.Token, saved.ExpiresAt, saved.User, false);
        logger.LogInformation("Chưa xác minh được phiên đăng nhập đã lưu (API tạm không trả lời); giữ phiên hiện có.");
    }

    /// <summary>Lưu kết quả POST /api/auth/login. Không nhận mật khẩu.</summary>
    public async Task SignInAsync(LoginResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.Token)) throw new ArgumentException("Phản hồi đăng nhập không có token.", nameof(response));
        var expires = response.ExpiresAt.Kind switch
        {
            DateTimeKind.Local => response.ExpiresAt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(response.ExpiresAt, DateTimeKind.Utc)
        };
        (Token, ExpiresAt, User, IsVerified, EndReason) = (response.Token, expires, response.User, true, AuthEndReason.None);
        _restore = Task.CompletedTask;
        await SafeSaveAsync();
        Changed?.Invoke();
    }

    public async Task SignOutAsync()
    {
        var was = Token is not null;
        ClearMemory();
        EndReason = AuthEndReason.SignedOut;
        _restore = Task.CompletedTask;
        await SafeClearAsync();
        if (was) Changed?.Invoke();
    }

    /// <summary>
    /// Token để gắn vào request lúc này; null nếu tab chưa đăng nhập. Token đã quá hạn → kết thúc phiên (Expired) ngay
    /// tại đây thay vì gửi request chắc chắn bị 401.
    /// </summary>
    internal async Task<string?> GetAccessTokenAsync()
    {
        await EnsureRestoredAsync();
        if (Token is null) return null;
        if (IsSignedIn) return Token;
        await EndAsync(Token);
        return null;
    }

    /// <summary>
    /// API trả 401 cho request đã gửi kèm <paramref name="tokenUsed"/>. Chỉ kết thúc phiên nếu đó vẫn là token hiện tại:
    /// một request cũ trả về sau khi người dùng đã đăng nhập lại không được xóa phiên mới. Gọi nhiều lần chỉ báo một lần.
    /// </summary>
    internal Task HandleUnauthorizedAsync(string? tokenUsed) => EndAsync(tokenUsed);

    /// <summary>API trả 403 "password_change_required": cập nhật trạng thái để AuthGate hiện hướng dẫn, không xóa phiên.</summary>
    internal void HandlePasswordChangeRequired(string? tokenUsed)
    {
        if (Token is null || tokenUsed != Token || User is not { MustChangePassword: false } u) return;
        User = new MeDto { UserId = u.UserId, Email = u.Email, FullName = u.FullName, Role = u.Role, MustChangePassword = true, LastLoginAt = u.LastLoginAt };
        Changed?.Invoke();
    }

    private async Task EndAsync(string? tokenUsed)
    {
        if (Token is null || (tokenUsed is not null && tokenUsed != Token)) return;
        ClearMemory();                      // trước mọi await: 401 song song thấy Token = null và dừng
        EndReason = AuthEndReason.Expired;
        _restore = Task.CompletedTask;
        await SafeClearAsync();
        Expired?.Invoke();
        Changed?.Invoke();
    }

    private void ClearMemory() => (Token, ExpiresAt, User, IsVerified) = (null, null, null, false);

    private async Task SafeSaveAsync()
    {
        if (Token is null || ExpiresAt is null || User is null) return;
        try { await store.SaveAsync(new AuthSnapshot(Token, ExpiresAt.Value, User)); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Vẫn đăng nhập trong circuit hiện tại; F5 sẽ phải đăng nhập lại.
            logger.LogWarning("Không lưu được phiên đăng nhập vào trình duyệt ({Error}).", error.GetType().Name);
        }
    }

    private async Task SafeClearAsync()
    {
        try { await store.ClearAsync(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("Không xóa được phiên đăng nhập trong trình duyệt ({Error}).", error.GetType().Name);
        }
    }
}
