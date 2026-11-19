using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

/// <summary>Các lời gọi xác thực mà AuthSession và trang đăng nhập cần. Tách interface để test thay bằng bản giả.</summary>
public interface IAuthApi
{
    /// <summary>POST /api/auth/login — không gửi Bearer.</summary>
    Task<ApiResult<LoginResponse>> LoginAsync(string email, string password, CancellationToken ct = default);

    /// <summary>GET /api/auth/me với một token cụ thể (xác minh phiên đã lưu trước khi dùng).</summary>
    Task<ApiResult<MeDto>> GetMeAsync(string token, CancellationToken ct = default);
}

/// <summary>
/// B27: gọi API đăng nhập của A28. Client này KHÔNG được gắn AuthSession (tránh vòng phụ thuộc và để 401 của
/// đăng nhập sai không bị hiểu là "phiên hết hạn"). B28/B29 thêm đổi mật khẩu, đọc lại người dùng tại đây.
/// Không ghi log email, mật khẩu hay token; log của ApiClientBase chỉ có đường dẫn và mã trạng thái.
/// </summary>
public sealed class AuthApiClient(HttpClient http, ILogger<AuthApiClient> logger) : ApiClientBase(http, logger), IAuthApi
{
    public Task<ApiResult<LoginResponse>> LoginAsync(string email, string password, CancellationToken ct = default) =>
        PostJsonAsync<LoginResponse>("api/auth/login", new LoginRequest { Email = email, Password = password }, ct);

    public Task<ApiResult<MeDto>> GetMeAsync(string token, CancellationToken ct = default) =>
        GetWithBearerAsync<MeDto>("api/auth/me", token, ct);
}
