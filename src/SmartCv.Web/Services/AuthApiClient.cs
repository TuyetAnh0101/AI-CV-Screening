using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

/// <summary>Các lời gọi xác thực mà AuthSession và trang đăng nhập cần. Tách interface để test thay bằng bản giả.</summary>
/// <summary>Giao diện B53 độc lập, không làm thay đổi test doubles của IAuthApi cũ.</summary>
public interface IRegistrationApi
{
    Task<ApiResult<RegistrationSettingsResponse>> GetRegistrationSettingsAsync(CancellationToken ct = default);
    Task<ApiResult<RegistrationNotice>> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<ApiResult<RegistrationNotice>> ResendVerificationAsync(string email, CancellationToken ct = default);
    Task<ApiResult<LoginResponse>> VerifyEmailAsync(string token, CancellationToken ct = default);
}

/// <summary>B54: public recovery API, separate interface so legacy IAuthApi test doubles remain compatible.</summary>
public interface IAuthRecoveryApi
{
    Task<ApiResult<SmartCv.Core.Contracts.AccountNotice>> ForgotPasswordAsync(string email, CancellationToken ct = default);
    Task<ApiResult<SmartCv.Core.Contracts.AccountNotice>> ResetPasswordAsync(string token, string newPassword, CancellationToken ct = default);
}

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
public sealed class AuthApiClient(HttpClient http, ILogger<AuthApiClient> logger) : ApiClientBase(http, logger), IAuthApi, IRegistrationApi, IAuthRecoveryApi
{
    // B53: tất cả endpoint công khai, không gắn Bearer; giữ tên đường dẫn cố định, không log token.
    public Task<ApiResult<RegistrationSettingsResponse>> GetRegistrationSettingsAsync(CancellationToken ct = default) =>
        GetAsync<RegistrationSettingsResponse>("api/auth/registration-settings", ct);

    public Task<ApiResult<RegistrationNotice>> RegisterAsync(RegisterRequest request, CancellationToken ct = default) =>
        PostJsonAsync<RegistrationNotice>("api/auth/register", request, ct);

    public Task<ApiResult<RegistrationNotice>> ResendVerificationAsync(string email, CancellationToken ct = default) =>
        PostJsonAsync<RegistrationNotice>("api/auth/resend-verification", new ResendVerificationRequest { Email = email }, ct);

    public Task<ApiResult<LoginResponse>> VerifyEmailAsync(string token, CancellationToken ct = default) =>
        PostJsonAsync<LoginResponse>("api/auth/verify-email", new VerifyEmailRequest { Token = token }, ct);

    // B54: token va mat khau chi nam trong JSON body; KHONG dua vao URL hay log.
    public Task<ApiResult<SmartCv.Core.Contracts.AccountNotice>> ForgotPasswordAsync(string email, CancellationToken ct = default) =>
        PostJsonAsync<SmartCv.Core.Contracts.AccountNotice>("api/auth/forgot-password", new ForgotPasswordRequest { Email = email }, ct);

    public Task<ApiResult<SmartCv.Core.Contracts.AccountNotice>> ResetPasswordAsync(string token, string newPassword, CancellationToken ct = default) =>
        PostJsonAsync<SmartCv.Core.Contracts.AccountNotice>("api/auth/reset-password", new ResetPasswordRequest { Token = token, NewPassword = newPassword }, ct);

    public Task<ApiResult<LoginResponse>> LoginAsync(string email, string password, CancellationToken ct = default) =>
        PostJsonAsync<LoginResponse>("api/auth/login", new LoginRequest { Email = email, Password = password }, ct);

    public Task<ApiResult<MeDto>> GetMeAsync(string token, CancellationToken ct = default) =>
        GetWithBearerAsync<MeDto>("api/auth/me", token, ct);

    public Task<ApiResult<ChangePasswordResponse>> ChangePasswordAsync(string token, string currentPassword, string newPassword, CancellationToken ct = default) =>
        SendRequestAsync<ChangePasswordResponse>(
            () => JsonRequest(HttpMethod.Post, "api/auth/change-password", new ChangePasswordRequest { CurrentPassword = currentPassword, NewPassword = newPassword }),
            "api/auth/change-password", ct, bearer: token);
}
