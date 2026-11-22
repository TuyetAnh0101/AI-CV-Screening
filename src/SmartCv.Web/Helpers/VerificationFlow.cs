using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

public enum ResendOutcome { NotSent, Accepted, InvalidEmail, RateLimited, Disabled, Unavailable }

/// <summary>Kiểm soát gửi lại email. Không cho gửi trùng hoặc spam bằng double-click.</summary>
public sealed class ResendVerificationFlow(TimeProvider clock, RegistrationFlowState state)
{
    public bool Submitting { get; private set; }
    public bool Disabled { get; private set; }
    public string EmailInput { get; set; } = "";
    public string? EmailError { get; private set; }
    public string? Message { get; private set; }
    public bool IsError { get; private set; }
    public bool CanSubmit => !Submitting && !Disabled && state.CooldownSeconds == 0;

    public async Task<ResendOutcome> SubmitAsync(IRegistrationApi api, CancellationToken ct = default)
    {
        if (!CanSubmit) return ResendOutcome.NotSent;
        EmailError = null; Message = null; IsError = false;
        var email = RegistrationRules.NormalizeResendEmail(
            !string.IsNullOrWhiteSpace(EmailInput) ? EmailInput : state.Email);
        if (email is null)
        {
            EmailError = "Nhập email hợp lệ để gửi lại liên kết xác minh.";
            return ResendOutcome.InvalidEmail;
        }
        Submitting = true;
        ApiResult<RegistrationNotice> result;
        try { result = await api.ResendVerificationAsync(email, ct); }
        finally { Submitting = false; }

        if (result.IsSuccess)
        {
            state.Remember(email);
            state.StartCooldown(TimeSpan.FromSeconds(60));
            Message = "Nếu email cần xác minh, SCApp sẽ gửi một liên kết mới. Hãy kiểm tra cả thư rác.";
            return ResendOutcome.Accepted;
        }
        if (result.StatusCode == 429)
        {
            state.StartCooldown(result.RetryAfter is { } delay && delay > TimeSpan.Zero ? delay : TimeSpan.FromMinutes(10));
            IsError = true;
            Message = "Đã đạt giới hạn gửi lại. Vui lòng thử sau khi hết thời gian chờ.";
            return ResendOutcome.RateLimited;
        }
        if (result.StatusCode == 404 && result.ErrorCode == "registration_disabled")
        {
            Disabled = true;
            IsError = true;
            Message = "Đăng ký hiện chưa được mở. Liên kết xác minh đã cấp vẫn có thể sử dụng.";
            return ResendOutcome.Disabled;
        }
        IsError = true;
        Message = "Chưa thể gửi yêu cầu. Nếu email đến chậm, vui lòng đợi và kiểm tra lại hộp thư.";
        return ResendOutcome.Unavailable;
    }
}

public enum VerifyOutcome { Verified, Expired, Invalid, Retryable, RateLimited }

/// <summary>Thông báo dựa theo ErrorCode từ A56; không phụ thuộc câu chữ server.</summary>
public static class VerificationResultRules
{
    public static VerifyOutcome Classify(ApiResult<LoginResponse> result) => result.IsSuccess && result.Data is { Token.Length: > 0 }
        ? VerifyOutcome.Verified
        : (result.StatusCode, result.ErrorCode) switch
        {
            (400, "token_expired") => VerifyOutcome.Expired,
            (400, "token_invalid") => VerifyOutcome.Invalid,
            (429, _) => VerifyOutcome.RateLimited,
            _ => VerifyOutcome.Retryable
        };
}
