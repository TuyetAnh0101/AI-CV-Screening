using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

/// <summary>Ô cần đưa con trỏ tới sau khi gửi form đổi mật khẩu.</summary>
public enum PasswordField { None, Current, New, Confirm, Submit }

/// <summary>Kết quả một lần bấm "Lưu mật khẩu mới".</summary>
public enum ChangePasswordOutcome
{
    /// <summary>Không gửi: đang gửi, đang bị giới hạn, hoặc còn lỗi trên form.</summary>
    NotSent,
    /// <summary>API xác nhận đã đổi và trả token mới (<see cref="ChangePasswordForm.Result"/>).</summary>
    Changed,
    /// <summary>API từ chối dữ liệu (400) — chắc chắn CHƯA đổi.</summary>
    Invalid,
    /// <summary>401: phiên không còn hiệu lực (bị khóa, đổi quyền, hết hạn) — nơi gọi kết thúc phiên.</summary>
    SessionEnded,
    /// <summary>429: thử quá nhiều lần — chắc chắn CHƯA đổi.</summary>
    RateLimited,
    /// <summary>Lỗi đã biết là CHƯA ghi (403, 409, 413, 415...).</summary>
    Failed,
    /// <summary>Không biết máy chủ đã đổi hay chưa (mất kết nối, quá giờ, 5xx, phản hồi hỏng). KHÔNG tự gửi lại.</summary>
    Unknown
}

/// <summary>Kết luận sau khi kiểm tra trạng thái (chỉ đọc, GET /api/auth/me bằng token cũ) cho trường hợp Unknown.</summary>
public enum UnknownResolution
{
    /// <summary>Token cũ vẫn dùng được → máy chủ chưa đổi mật khẩu. Có thể thử lại.</summary>
    NotChanged,
    /// <summary>Token cũ bị từ chối → rất có thể đã đổi (SecurityStamp mới). Phải đăng nhập lại.</summary>
    ProbablyChanged,
    /// <summary>Vẫn không liên lạc được máy chủ.</summary>
    StillUnknown
}

public sealed record PasswordRuleCheck(string Text, bool Met);

/// <summary>
/// B29: trạng thái và luật của form đổi mật khẩu (A30 POST /api/auth/change-password), tách khỏi trang để test không cần trình duyệt.
/// - Luật mật khẩu mới = đúng <see cref="PasswordRules"/> của Core (API dùng cùng hàm) + khác mật khẩu hiện tại.
/// - Không bao giờ trim mật khẩu; không giữ mật khẩu sau khi API đã trả lời; không gửi trùng; không tự gửi lại.
/// - Kết quả không rõ (mất mạng, 5xx) được kiểm tra bằng /api/auth/me CHỈ ĐỌC, không gửi lại thao tác đổi mật khẩu.
/// </summary>
public sealed class ChangePasswordForm(TimeProvider time)
{
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);

    public string Current { get; set; } = "";
    public string New { get; set; } = "";
    public string Confirm { get; set; } = "";
    public bool ShowCurrent { get; set; }
    public bool ShowNew { get; set; }
    public bool ShowConfirm { get; set; }

    public bool Submitting { get; private set; }
    public string? CurrentError { get; private set; }
    public string? NewError { get; private set; }
    public string? ConfirmError { get; private set; }
    public string? FormError { get; private set; }
    public PasswordField FocusTarget { get; private set; }
    public DateTimeOffset? RetryAt { get; private set; }
    public ChangePasswordResponse? Result { get; private set; }

    /// <summary>Đang chờ biết kết quả của một lần gửi không rõ — khóa form cho tới khi kiểm tra xong.</summary>
    public bool Unresolved { get; private set; }

    public bool IsRateLimited => RetryAt is { } at && at > time.GetUtcNow();
    public int RetrySecondsLeft => IsRateLimited ? (int)Math.Ceiling((RetryAt!.Value - time.GetUtcNow()).TotalSeconds) : 0;
    /// <summary>API trả 401: phiên đã hết — không gửi gì thêm từ form này.</summary>
    public bool SessionEnded { get; private set; }

    public bool CanSubmit => !Submitting && !IsRateLimited && !Unresolved && !SessionEnded && Result is null;

    /// <summary>Danh sách luật hiển thị cạnh ô mật khẩu mới (cập nhật khi gõ).</summary>
    public IReadOnlyList<PasswordRuleCheck> Rules(string? email) =>
    [
        new($"Từ {PasswordRules.MinLength} đến {PasswordRules.MaxLength} ký tự", New.Length is >= PasswordRules.MinLength and <= PasswordRules.MaxLength),
        new("Có cả chữ và số", New.Any(char.IsLetter) && New.Any(char.IsDigit)),
        new("Không có khoảng trắng ở đầu hoặc cuối", New.Length > 0 && New.Trim() == New),
        new("Không trùng email đăng nhập", New.Length > 0 && (string.IsNullOrWhiteSpace(email) || !string.Equals(New, email.Trim(), StringComparison.OrdinalIgnoreCase))),
        new("Khác mật khẩu hiện tại", New.Length > 0 && New != Current),
    ];

    /// <summary>Kiểm tra phía Web trước khi gửi. true = gửi được.</summary>
    public bool Validate(string? email)
    {
        CurrentError = NewError = ConfirmError = null;
        if (Current.Length == 0) CurrentError = "Vui lòng nhập mật khẩu hiện tại.";
        else if (Current.Length > PasswordRules.MaxLength) CurrentError = $"Mật khẩu tối đa {PasswordRules.MaxLength} ký tự.";

        NewError = New.Length == 0 ? "Vui lòng nhập mật khẩu mới." : PasswordRules.Validate(New, email);
        if (NewError is null && New == Current) NewError = "Mật khẩu mới phải khác mật khẩu hiện tại.";

        // Chỉ báo lỗi ô nhập lại khi mật khẩu mới đã hợp lệ — tránh hai lỗi chồng nhau cho cùng một việc.
        if (NewError is null && Confirm.Length == 0) ConfirmError = "Vui lòng nhập lại mật khẩu mới.";
        else if (NewError is null && Confirm != New) ConfirmError = "Hai lần nhập mật khẩu mới chưa khớp.";

        FocusTarget = CurrentError is not null ? PasswordField.Current : NewError is not null ? PasswordField.New
            : ConfirmError is not null ? PasswordField.Confirm : PasswordField.None;
        return CurrentError is null && NewError is null && ConfirmError is null;
    }

    public async Task<ChangePasswordOutcome> SubmitAsync(IAuthApi api, string token, string? email, CancellationToken ct = default)
    {
        if (!CanSubmit) return ChangePasswordOutcome.NotSent;
        FormError = null;
        if (!Validate(email)) return ChangePasswordOutcome.NotSent;

        Submitting = true;
        ApiResult<ChangePasswordResponse> result;
        try { result = await api.ChangePasswordAsync(token, Current, New, ct); }
        finally { Submitting = false; }

        if (result.IsSuccess && result.Data is { Token.Length: > 0, User: not null } ok)
        {
            Result = ok;
            ClearPasswords();
            FocusTarget = PasswordField.None;
            return ChangePasswordOutcome.Changed;
        }

        FocusTarget = PasswordField.Submit;
        if (result.IsSuccess || result.FailureKind is ApiFailureKind.Network or ApiFailureKind.Timeout or ApiFailureKind.InvalidData
            || result.StatusCode is >= 500)
        {
            // Máy chủ có thể đã đổi rồi mới lỗi. Không gửi lại; khóa form tới khi kiểm tra trạng thái.
            Unresolved = true;
            ClearPasswords();
            FormError = "Chưa nhận được xác nhận từ máy chủ. SCApp đang kiểm tra mật khẩu đã được đổi hay chưa — không gửi lại tự động.";
            return ChangePasswordOutcome.Unknown;
        }

        switch (result.StatusCode)
        {
            case 400:
                var errors = result.ValidationErrors;
                if (errors.ContainsKey("currentPassword")) { CurrentError = "Mật khẩu hiện tại không đúng."; Current = ""; }
                if (errors.TryGetValue("newPassword", out var newErrors) && newErrors.Length > 0) NewError = newErrors[0];
                FormError = CurrentError is null && NewError is null ? "Thông tin chưa hợp lệ. Kiểm tra lại các ô rồi thử lại." : null;
                FocusTarget = CurrentError is not null ? PasswordField.Current : NewError is not null ? PasswordField.New : PasswordField.Submit;
                return ChangePasswordOutcome.Invalid;
            case 401:
                SessionEnded = true;
                ClearPasswords();
                FormError = AuthText.SessionExpired;
                return ChangePasswordOutcome.SessionEnded;
            case 429:
                RetryAt = time.GetUtcNow() + (result.RetryAfter is { } wait && wait > TimeSpan.Zero ? wait : DefaultRetryAfter);
                Current = "";
                FormError = "Bạn đã thử đổi mật khẩu quá nhiều lần. Vui lòng đợi rồi thử lại.";
                return ChangePasswordOutcome.RateLimited;
            case 409:
                FormError = "Tài khoản vừa được cập nhật ở nơi khác nên mật khẩu chưa được đổi. Vui lòng thử lại.";
                return ChangePasswordOutcome.Failed;
            case 403:
                ClearPasswords();
                FormError = "Tài khoản hiện không được phép đổi mật khẩu. Vui lòng liên hệ quản trị viên (Admin).";
                return ChangePasswordOutcome.Failed;
            default:
                FormError = "Không đổi được mật khẩu. Vui lòng thử lại.";
                return ChangePasswordOutcome.Failed;
        }
    }

    /// <summary>
    /// Sau Unknown: hỏi /api/auth/me bằng token CŨ (chỉ đọc). 200 → chưa đổi (mở khóa form để thử lại); 401 → đã đổi / phiên
    /// đã bị thu hồi (phải đăng nhập lại); lỗi khác → vẫn chưa biết.
    /// </summary>
    public async Task<UnknownResolution> ResolveUnknownAsync(IAuthApi api, string token, CancellationToken ct = default)
    {
        var me = await api.GetMeAsync(token, ct);
        if (me.IsSuccess)
        {
            Unresolved = false;
            FormError = "Mật khẩu CHƯA được thay đổi (máy chủ chưa ghi nhận). Bạn có thể nhập lại và thử lần nữa.";
            return UnknownResolution.NotChanged;
        }
        if (me.StatusCode == 401)
        {
            FormError = "Có thể mật khẩu đã được đổi nhưng SCApp không nhận được xác nhận. Vui lòng đăng nhập lại — hãy thử mật khẩu mới trước.";
            return UnknownResolution.ProbablyChanged;
        }
        FormError = "Vẫn chưa liên lạc được máy chủ nên chưa biết mật khẩu đã đổi hay chưa. Kiểm tra kết nối rồi bấm \"Kiểm tra lại\", hoặc đăng nhập lại.";
        return UnknownResolution.StillUnknown;
    }

    /// <summary>Hết thời gian chờ 429.</summary>
    public void ClearExpiredRateLimit()
    {
        if (RetryAt is null || IsRateLimited) return;
        RetryAt = null;
        FormError = null;
    }

    public void ClearFocusTarget() => FocusTarget = PasswordField.None;

    /// <summary>Xóa mọi mật khẩu khỏi bộ nhớ của form (sau khi API trả lời, khi rời trang).</summary>
    public void ClearPasswords() => (Current, New, Confirm) = ("", "", "");
}
