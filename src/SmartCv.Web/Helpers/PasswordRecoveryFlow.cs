using SmartCv.Core.Auth;
using SmartCv.Core.Candidates;
using RecoveryNotice = SmartCv.Core.Contracts.AccountNotice;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

public enum ForgotPasswordOutcome { NotSent, Accepted, RateLimited, Failed }
public enum ResetPasswordOutcome { NotSent, Changed, Invalid, Expired, RateLimited, Failed, Unknown }

/// <summary>
/// B54: request công khai. Mọi email hợp lệ đều nhìn thấy cùng một thông báo 202; không tiết lộ người dùng tồn tại.
/// Tránh gửi trùng trong lúc đang xử lý, và chờ ít nhất 60 giây sau mỗi lần máy chủ có thể đã nhận request.
/// </summary>
public sealed class ForgotPasswordFlow(TimeProvider clock)
{
    public const string GenericAccepted = "Nếu email có tài khoản, SCApp đã gửi hướng dẫn đặt lại mật khẩu. Hãy kiểm tra hộp thư và thư rác.";
    public string Email { get; set; } = "";
    public string? EmailError { get; private set; }
    public string? FormError { get; private set; }
    public bool Accepted { get; private set; }
    public bool Submitting { get; private set; }
    public DateTimeOffset? RetryAt { get; private set; }
    public int RetrySecondsLeft => RetryAt is { } at && at > clock.GetUtcNow()
        ? (int)Math.Ceiling((at - clock.GetUtcNow()).TotalSeconds) : 0;
    public bool CanSubmit => !Submitting && RetrySecondsLeft == 0;

    public async Task<ForgotPasswordOutcome> SubmitAsync(IAuthRecoveryApi api, CancellationToken ct = default)
    {
        if (!CanSubmit) return ForgotPasswordOutcome.NotSent;
        EmailError = FormError = null;
        // Validation chỉ là kiểm tra cú pháp, không yêu cầu backend xác nhận có tài khoản hay không.
        var normalized = Email.Any(char.IsControl) ? null : CandidateRules.NormalizeEmail(Email);
        if (normalized is null)
        {
            EmailError = "Nhập địa chỉ email hợp lệ, tối đa 254 ký tự.";
            return ForgotPasswordOutcome.NotSent;
        }

        Submitting = true;
        ApiResult<RecoveryNotice> result;
        try { result = await api.ForgotPasswordAsync(normalized, ct); }
        finally { Submitting = false; }

        // Bất kỳ lần gửi nào nhận 202 hoặc không rõ kết quả đều có thể đã tạo email.
        RetryAt = clock.GetUtcNow().AddSeconds(60);
        if (result.IsSuccess)
        {
            Accepted = true;
            Email = ""; // Không cần giữ địa chỉ cá nhân sau khi gửi.
            return ForgotPasswordOutcome.Accepted;
        }
        if (result.StatusCode == 429)
        {
            var serverWait = result.RetryAfter is { } wait && wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(60);
            RetryAt = clock.GetUtcNow().Add(serverWait > TimeSpan.FromSeconds(60) ? serverWait : TimeSpan.FromSeconds(60));
            FormError = "Có quá nhiều yêu cầu. Hãy đợi đến khi hết thời gian chờ rồi thử lại.";
            return ForgotPasswordOutcome.RateLimited;
        }
        FormError = result.FailureKind is ApiFailureKind.Network or ApiFailureKind.Timeout or ApiFailureKind.InvalidData
            ? "Chưa nhận được xác nhận từ SCApp. Có thể thư đã được gửi; hãy kiểm tra hộp thư trước khi thử lại."
            : "Chưa thể xử lý yêu cầu lúc này. Hãy kiểm tra kết nối và thử lại sau.";
        return ForgotPasswordOutcome.Failed;
    }
}

/// <summary>
/// B54: chỉ gửi reset 1 lần cho tới khi có kết quả chắc chắn. Nếu phản hồi không rõ (mạng/5xx),
/// không tự lặp lại vì token có thể đã tiêu thụ. Mật khẩu không bao giờ được trim hay đưa vào URL.
/// </summary>
public sealed class ResetPasswordFlow(TimeProvider clock)
{
    public string NewPassword { get; set; } = "";
    public string ConfirmPassword { get; set; } = "";
    public bool ShowNew { get; set; }
    public bool ShowConfirm { get; set; }
    public bool Submitting { get; private set; }
    public bool Completed { get; private set; }
    public bool Unresolved { get; private set; }
    public bool InvalidToken { get; private set; }
    public bool ExpiredToken { get; private set; }
    public string? NewError { get; private set; }
    public string? ConfirmError { get; private set; }
    public string? FormError { get; private set; }
    public DateTimeOffset? RetryAt { get; private set; }
    public int RetrySecondsLeft => RetryAt is { } at && at > clock.GetUtcNow()
        ? (int)Math.Ceiling((at - clock.GetUtcNow()).TotalSeconds) : 0;
    public bool CanSubmit => !Submitting && !Completed && !Unresolved && !InvalidToken && !ExpiredToken && RetrySecondsLeft == 0;

    /// <summary>Chỉ kiểm tra hình dạng token. Backend A57 xác minh hash, purpose, thời hạn và trạng thái sử dụng.</summary>
    public static bool LooksLikeToken(string? value) => value is { Length: 43 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public IReadOnlyList<PasswordRuleCheck> Rules =>
    [
        new("Từ 8 đến 128 ký tự", NewPassword.Length is >= 8 and <= 128),
        new("Có cả chữ và số", NewPassword.Any(char.IsLetter) && NewPassword.Any(char.IsDigit)),
        new("Không có khoảng trắng ở đầu hoặc cuối", NewPassword.Length > 0 && NewPassword.Trim() == NewPassword)
    ];

    public bool Validate()
    {
        NewError = PasswordRules.Validate(NewPassword);
        ConfirmError = null;
        if (NewError is null && ConfirmPassword.Length == 0) ConfirmError = "Vui lòng nhập lại mật khẩu mới.";
        else if (NewError is null && ConfirmPassword != NewPassword) ConfirmError = "Hai lần nhập mật khẩu chưa khớp.";
        return NewError is null && ConfirmError is null;
    }

    public void MissingOrInvalidLink()
    {
        InvalidToken = true;
        ClearSecrets();
        FormError = "Liên kết đặt lại mật khẩu không hợp lệ. Hãy yêu cầu liên kết mới.";
    }

    public async Task<ResetPasswordOutcome> SubmitAsync(IAuthRecoveryApi api, string? token, CancellationToken ct = default)
    {
        if (!CanSubmit) return ResetPasswordOutcome.NotSent;
        if (!LooksLikeToken(token)) { MissingOrInvalidLink(); return ResetPasswordOutcome.Invalid; }
        FormError = null;
        if (!Validate()) return ResetPasswordOutcome.NotSent;

        Submitting = true;
        ApiResult<RecoveryNotice> result;
        try { result = await api.ResetPasswordAsync(token!, NewPassword, ct); }
        finally { Submitting = false; }
        if (result.IsSuccess)
        {
            Completed = true;
            ClearSecrets();
            return ResetPasswordOutcome.Changed;
        }

        if (result.ErrorCode == "token_expired")
        {
            ExpiredToken = true;
            FormError = "Liên kết đã hết hạn (30 phút). Hãy yêu cầu gửi lại liên kết mới.";
            ClearSecrets();
            return ResetPasswordOutcome.Expired;
        }
        if (result.ErrorCode == "token_invalid")
        {
            InvalidToken = true;
            FormError = "Liên kết không hợp lệ hoặc đã được sử dụng. Hãy yêu cầu liên kết mới.";
            ClearSecrets();
            return ResetPasswordOutcome.Invalid;
        }
        if (result.StatusCode == 400)
        {
            NewError = result.ValidationErrors.TryGetValue("newPassword", out var messages) && messages.Length > 0
                ? messages[0] : "Mật khẩu mới không đáp ứng yêu cầu. Vui lòng chọn mật khẩu khác.";
            // Xóa mật khẩu đã bị từ chối để không giữ bí mật qua nhiều lần gửi.
            ClearSecrets();
            return ResetPasswordOutcome.Failed;
        }
        if (result.StatusCode == 429)
        {
            var wait = result.RetryAfter is { } delay && delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(60);
            RetryAt = clock.GetUtcNow().Add(wait);
            FormError = "Bạn đã thử quá nhiều lần. Hãy đợi rồi thử lại.";
            ClearSecrets();
            return ResetPasswordOutcome.RateLimited;
        }
        if (result.StatusCode is 409 or 413 or 415)
        {
            FormError = "Không thể đặt lại mật khẩu do dữ liệu hoặc tài khoản vừa thay đổi. Hãy yêu cầu liên kết mới nếu cần.";
            ClearSecrets();
            return ResetPasswordOutcome.Failed;
        }
        // 5xx, response bị hỏng, mất mạng hoặc timeout: không biết token đã được tiêu thụ hay chưa.
        Unresolved = true;
        FormError = "Chưa nhận được xác nhận. Mật khẩu có thể đã thay đổi. Hãy thử đăng nhập bằng mật khẩu mới; SCApp sẽ không tự gửi lại yêu cầu.";
        ClearSecrets();
        return ResetPasswordOutcome.Unknown;
    }

    public void ClearSecrets() => (NewPassword, ConfirmPassword) = ("", "");
}
