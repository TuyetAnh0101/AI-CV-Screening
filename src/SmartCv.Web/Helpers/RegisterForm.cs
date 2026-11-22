using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

public enum RegisterOutcome { NotSent, Accepted, Invalid, RateLimited, Disabled, Unavailable }
public enum RegisterField { None, FullName, Email, Password, ConfirmPassword, AcceptTerms, Submit }

/// <summary>Luồng đăng ký không cần render để kiểm thử. Validation dùng đúng RegistrationRules của A56.</summary>
public sealed class RegisterForm(TimeProvider clock)
{
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string ConfirmPassword { get; set; } = "";
    public bool AcceptTerms { get; set; }
    public bool ShowPassword { get; set; }
    public bool ShowConfirmPassword { get; set; }

    public bool Submitting { get; private set; }
    public bool Succeeded { get; private set; }
    public bool RegistrationDisabled { get; private set; }
    public string? NormalizedEmail { get; private set; }
    public string? FullNameError { get; private set; }
    public string? EmailError { get; private set; }
    public string? PasswordError { get; private set; }
    public string? ConfirmPasswordError { get; private set; }
    public string? AcceptTermsError { get; private set; }
    public string? FormError { get; private set; }
    public DateTimeOffset? RetryAt { get; private set; }
    public RegisterField FocusTarget { get; private set; }

    public bool IsRateLimited => RetryAt is { } at && at > clock.GetUtcNow();
    public int RetrySecondsLeft => IsRateLimited ? (int)Math.Ceiling((RetryAt!.Value - clock.GetUtcNow()).TotalSeconds) : 0;
    public bool CanSubmit => !Submitting && !Succeeded && !RegistrationDisabled && !IsRateLimited;

    public bool Validate()
    {
        FullNameError = EmailError = PasswordError = ConfirmPasswordError = AcceptTermsError = null;
        var (input, errors) = RegistrationRules.Validate(FullName, Email, Password, AcceptTerms);
        foreach (var item in errors)
            SetFieldError(item.Key, item.Value.FirstOrDefault());
        if (string.IsNullOrEmpty(ConfirmPassword)) ConfirmPasswordError = "Vui lòng nhập lại mật khẩu.";
        else if (ConfirmPassword != Password) ConfirmPasswordError = "Hai lần nhập mật khẩu chưa khớp.";

        NormalizedEmail = input?.Email;
        FocusTarget = ChooseFocus();
        return input is not null && ConfirmPasswordError is null;
    }

    public async Task<RegisterOutcome> SubmitAsync(IRegistrationApi api, CancellationToken ct = default)
    {
        if (!CanSubmit) return RegisterOutcome.NotSent;
        FormError = null;
        if (!Validate()) return RegisterOutcome.Invalid;
        Submitting = true;
        ApiResult<RegistrationNotice> result;
        try
        {
            result = await api.RegisterAsync(new RegisterRequest
            {
                FullName = FullName, Email = NormalizedEmail,
                Password = Password, AcceptTerms = AcceptTerms
            }, ct);
        }
        finally { Submitting = false; }

        if (result.IsSuccess)
        {
            Succeeded = true;
            Password = ConfirmPassword = "";
            FocusTarget = RegisterField.None;
            return RegisterOutcome.Accepted;
        }
        if (result.StatusCode == 400 && result.ValidationErrors.Count > 0)
        {
            foreach (var error in result.ValidationErrors)
                SetFieldError(error.Key, error.Value.FirstOrDefault());
            FocusTarget = ChooseFocus();
            FormError = FocusTarget == RegisterField.Submit ? "Kiểm tra lại thông tin và thử lần nữa." : null;
            return RegisterOutcome.Invalid;
        }
        if (result.StatusCode == 429)
        {
            RetryAt = clock.GetUtcNow().Add(result.RetryAfter is { } delay && delay > TimeSpan.Zero
                ? delay : TimeSpan.FromMinutes(10));
            FormError = "Bạn gửi yêu cầu quá nhanh. Vui lòng chờ hết thời gian giới hạn trước khi thử lại.";
            FocusTarget = RegisterField.Submit;
            return RegisterOutcome.RateLimited;
        }
        if (result.StatusCode == 404 && result.ErrorCode == "registration_disabled")
        {
            RegistrationDisabled = true;
            Password = ConfirmPassword = "";
            return RegisterOutcome.Disabled;
        }
        // Không tự thử lại thao tác ghi khi response không rõ; một lần đăng ký có thể đã được ghi thành công.
        FormError = result.FailureKind is ApiFailureKind.Network or ApiFailureKind.Timeout or ApiFailureKind.InvalidData
            || result.StatusCode is >= 500
            ? "Chưa nhận được xác nhận. Hãy kiểm tra hộp thư trước; nếu chưa có email, bạn có thể thử lại sau."
            : "Không thể tạo tài khoản lúc này. Vui lòng kiểm tra lại và thử sau.";
        FocusTarget = RegisterField.Submit;
        return RegisterOutcome.Unavailable;
    }

    public IReadOnlyList<(string Label, bool Met)> PasswordChecklist(PublicPasswordRules? server)
    {
        var min = server?.MinLength ?? PasswordRules.MinLength;
        var max = server?.MaxLength ?? PasswordRules.MaxLength;
        return [
            ($"Từ {min} đến {max} ký tự", Password.Length >= min && Password.Length <= max),
            ("Có ít nhất một chữ cái", ! (server?.RequiresLetter ?? true) || Password.Any(char.IsLetter)),
            ("Có ít nhất một chữ số", ! (server?.RequiresDigit ?? true) || Password.Any(char.IsDigit)),
            ("Không có khoảng trắng ở đầu hoặc cuối", Password.Length > 0 && Password.Trim() == Password),
            ("Không trùng email", Password.Length > 0 && !string.Equals(Password, Email.Trim(), StringComparison.OrdinalIgnoreCase))
        ];
    }

    public void ClearExpiredRateLimit()
    {
        if (RetryAt is not null && !IsRateLimited) { RetryAt = null; FormError = null; }
    }
    public void ClearFocusTarget() => FocusTarget = RegisterField.None;
    public void ClearPasswords() => Password = ConfirmPassword = "";

    private void SetFieldError(string field, string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        switch (field)
        {
            case "fullName": FullNameError = message; break;
            case "email": EmailError = message; break;
            case "password": PasswordError = message; break;
            case "confirmPassword": ConfirmPasswordError = message; break;
            case "acceptTerms": AcceptTermsError = message; break;
        }
    }
    private RegisterField ChooseFocus() => FullNameError is not null ? RegisterField.FullName
        : EmailError is not null ? RegisterField.Email
        : PasswordError is not null ? RegisterField.Password
        : ConfirmPasswordError is not null ? RegisterField.ConfirmPassword
        : AcceptTermsError is not null ? RegisterField.AcceptTerms : RegisterField.Submit;
}
