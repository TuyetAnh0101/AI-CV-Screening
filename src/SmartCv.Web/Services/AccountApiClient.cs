using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

/// <summary>
/// B55: client CÓ phiên AuthSession của đúng circuit.
/// Không dùng AuthApiClient cho các thao tác đăng nhập sẵn vì AuthApiClient là public client.
/// ApiClientBase tự gửi Bearer và xử lý 401 / 403 như những trang nghiệp vụ khác.
/// </summary>
public sealed class AccountApiClient(HttpClient http, ILogger<AccountApiClient> logger) : ApiClientBase(http, logger)
{
    public Task<ApiResult<ProfileDto>> GetProfileAsync(CancellationToken ct = default) =>
        GetAsync<ProfileDto>("api/auth/me/profile", ct);

    public Task<ApiResult<ProfileDto>> UpdateProfileAsync(string fullName, CancellationToken ct = default) =>
        PutJsonAsync<ProfileDto>("api/auth/me/profile", new UpdateMyProfileRequest { FullName = fullName }, ct);

    /// <summary>Đường dẫn backend A57 thực tế là /me/password-setup, không phải /password-setup-email.</summary>
    public Task<ApiResult<SmartCv.Core.Contracts.AccountNotice>> RequestPasswordSetupEmailAsync(CancellationToken ct = default) =>
        PostJsonAsync<SmartCv.Core.Contracts.AccountNotice>("api/auth/me/password-setup", new { }, ct);
    // B55 part2: three authenticated A58 endpoints; DeleteAsync handles HTTP 204.
    public Task<ApiResult<List<LinkedLoginDto>>> GetLoginsAsync(CancellationToken ct = default) =>
        GetAsync<List<LinkedLoginDto>>("api/auth/me/logins", ct);

    public Task<ApiResult<ExternalLinkStartDto>> CreateLinkTicketAsync(string provider, CancellationToken ct = default) =>
        PostJsonAsync<ExternalLinkStartDto>("api/auth/external/link-ticket", new { Provider = provider }, ct);

    public Task<ApiResult<bool>> UnlinkAsync(string provider, CancellationToken ct = default) =>
        DeleteAsync("api/auth/me/logins/" + Uri.EscapeDataString(provider), ct);
}
