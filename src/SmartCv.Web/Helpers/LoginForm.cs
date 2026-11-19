using SmartCv.Core.Auth;
using SmartCv.Core.Candidates;
using SmartCv.Core.Contracts;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

/// <summary>Ô cần đưa con trỏ tới sau khi gửi form.</summary>
public enum LoginField { None, Email, Password, Submit }

/// <summary>
/// B27: trạng thái và luật của form đăng nhập, tách khỏi Login.razor để test không cần trình duyệt.
/// - Kiểm tra email bằng đúng luật của Core (CandidateRules.NormalizeEmail — API A28 dùng cùng hàm).
/// - Không gửi trùng: đang gửi hoặc đang bị giới hạn (429) thì <see cref="SubmitAsync"/> không gọi API.
/// - Mật khẩu bị xóa khỏi bộ nhớ sau mỗi lần gửi được API trả lời (thành công, sai thông tin, bị khóa).
/// </summary>
public sealed class LoginForm(TimeProvider time)
{
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);

    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public bool ShowPassword { get; set; }

    public bool Submitting { get; private set; }
    public string? EmailError { get; private set; }
    public string? PasswordError { get; private set; }
    public string? FormError { get; private set; }
    public LoginField FocusTarget { get; private set; }
    public DateTimeOffset? RetryAt { get; private set; }

    public bool IsRateLimited => RetryAt is { } at && at > time.GetUtcNow();
    public int RetrySecondsLeft => RetryAt is { } at && at > time.GetUtcNow() ? (int)Math.Ceiling((at - time.GetUtcNow()).TotalSeconds) : 0;
    public bool CanSubmit => !Submitting && !IsRateLimited;

    /// <summary>Kiểm tra phía Web trước khi gửi. Trả email đã chuẩn hóa, hoặc null khi còn lỗi.</summary>
    public string? Validate()
    {
        EmailError = PasswordError = null;
        string? email = null;
        if (string.IsNullOrWhiteSpace(Email)) EmailError = "Vui lòng nhập email.";
        else if ((email = CandidateRules.NormalizeEmail(Email)) is null) EmailError = "Email chưa đúng định dạng (ví dụ: ten@congty.vn).";

        if (string.IsNullOrEmpty(Password)) PasswordError = "Vui lòng nhập mật khẩu.";
        else if (Password.Length > PasswordRules.MaxLength) PasswordError = $"Mật khẩu tối đa {PasswordRules.MaxLength} ký tự.";

        FocusTarget = EmailError is not null ? LoginField.Email : PasswordError is not null ? LoginField.Password : LoginField.None;
        return EmailError is null && PasswordError is null ? email : null;
    }

    /// <summary>Gửi đăng nhập. Trả phản hồi khi thành công (nơi gọi lưu phiên), null khi không gửi hoặc thất bại.</summary>
    public async Task<LoginResponse?> SubmitAsync(IAuthApi api, CancellationToken ct = default)
    {
        if (!CanSubmit) return null;
        FormError = null;
        var email = Validate();
        if (email is null) return null;

        Submitting = true;
        ApiResult<LoginResponse> result;
        try { result = await api.LoginAsync(email, Password, ct); }
        finally { Submitting = false; }

        if (result.IsSuccess && result.Data is { Token.Length: > 0 } ok)
        {
            Password = "";
            FocusTarget = LoginField.None;
            return ok;
        }

        FormError = result.IsSuccess
            ? "Máy chủ trả về dữ liệu không hợp lệ. Vui lòng thử lại sau."
            : AuthText.LoginError(result);
        FocusTarget = LoginField.Submit;

        switch (result.StatusCode)
        {
            case 400:
                if (result.ValidationErrors.ContainsKey("email")) EmailError = "Email chưa đúng định dạng.";
                if (result.ValidationErrors.ContainsKey("password")) PasswordError = $"Mật khẩu là bắt buộc và tối đa {PasswordRules.MaxLength} ký tự.";
                FocusTarget = EmailError is not null ? LoginField.Email : PasswordError is not null ? LoginField.Password : LoginField.Submit;
                break;
            case 401 or 403:
                Password = "";
                FocusTarget = LoginField.Password;
                break;
            case 429:
                RetryAt = time.GetUtcNow() + (result.RetryAfter is { } wait && wait > TimeSpan.Zero ? wait : DefaultRetryAfter);
                Password = "";
                break;
        }
        return null;
    }

    /// <summary>Hết thời gian chờ 429: bỏ câu báo giới hạn để người dùng thử lại.</summary>
    public void ClearExpiredRateLimit()
    {
        if (RetryAt is null || IsRateLimited) return;
        RetryAt = null;
        FormError = null;
    }

    public void ClearFocusTarget() => FocusTarget = LoginField.None;
}
