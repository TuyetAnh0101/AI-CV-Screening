using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

/// <summary>Các lời gọi xác thực mà AuthSession và trang đăng nhập cần. Tách interface để test thay bằng bản giả.</summary>
public interface IAuthApi
{
    /// <summary>POST /api/auth/login — không gửi Bearer.</summary>
    Task<ApiResult<LoginResponse>> LoginAsync(string email, string password, CancellationToken ct = default);

    /// <summary>GET /api/auth/me với một token cụ thể (xác minh phiên đã lưu trước khi dùng).</summary>
    Task<ApiResult<MeDto>> GetMeAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// B29: POST /api/auth/change-password với đúng token của phiên đang dùng. Thành công → token MỚI (token cũ hết hiệu lực
    /// vì SecurityStamp đổi). Không bao giờ tự gửi lại: đổi mật khẩu là thao tác ghi thông tin đăng nhập.
    /// </summary>
    Task<ApiResult<ChangePasswordResponse>> ChangePasswordAsync(string token, string currentPassword, string newPassword, CancellationToken ct = default);
}

/// <summary>
/// B27: gọi API đăng nhập của A28. Client này KHÔNG được gắn AuthSession (tránh vòng phụ thuộc và để 401 của
/// đăng nhập sai không bị hiểu là "phiên hết hạn"). B29: đổi mật khẩu (A30) — người gọi tự quyết xử lý 401.
/// Không ghi log email, mật khẩu hay token; log của ApiClientBase chỉ có đường dẫn và mã trạng thái.
/// </summary>
public sealed class AuthApiClient(HttpClient http, ILogger<AuthApiClient> logger) : ApiClientBase(http, logger), IAuthApi
{
    public Task<ApiResult<LoginResponse>> LoginAsync(string email, string password, CancellationToken ct = default) =>
        PostJsonAsync<LoginResponse>("api/auth/login", new LoginRequest { Email = email, Password = password }, ct);

    public Task<ApiResult<MeDto>> GetMeAsync(string token, CancellationToken ct = default) =>
        GetWithBearerAsync<MeDto>("api/auth/me", token, ct);

    public Task<ApiResult<ChangePasswordResponse>> ChangePasswordAsync(string token, string currentPassword, string newPassword, CancellationToken ct = default) =>
        SendRequestAsync<ChangePasswordResponse>(
            () => JsonRequest(HttpMethod.Post, "api/auth/change-password", new ChangePasswordRequest { CurrentPassword = currentPassword, NewPassword = newPassword }),
            "api/auth/change-password", ct, bearer: token);
}
