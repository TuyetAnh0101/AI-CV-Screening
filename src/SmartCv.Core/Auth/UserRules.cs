using System.Security.Cryptography;
using SmartCv.Core.Candidates;

namespace SmartCv.Core.Auth;

/// <summary>Thông tin tài khoản đã kiểm tra và chuẩn hóa.</summary>
public sealed record UserInput(string Email, string FullName, string Role);

/// <summary>Tài khoản trong phép kiểm tra quyền (không cần cả entity).</summary>
public sealed record UserSnapshot(int UserId, string Role, bool IsActive);

/// <summary>
/// A30: quy tắc quản lý tài khoản. Hàm thuần → unit test được.
///   - Email đúng định dạng (chữ thường), họ tên bắt buộc ≤ 150, vai trò Admin / HR.
///   - Không tự khóa mình, không tự đổi vai trò của mình (tránh tự gỡ quyền Admin rồi không ai sửa được).
///   - Luôn còn ít nhất MỘT Admin đang hoạt động: không khóa / không hạ quyền Admin cuối cùng.
/// </summary>
public static class UserRules
{
    public const int MaxNameLength = 150;

    public static (UserInput? Input, Dictionary<string, string[]> Errors) ValidateCreate(string? email, string? fullName, string? role, string? password)
    {
        var errors = new Dictionary<string, string[]>();
        var e = CandidateRules.NormalizeEmail(email);
        if (e is null) errors["email"] = [string.IsNullOrWhiteSpace(email) ? "Email là bắt buộc." : "Email không đúng định dạng."];
        ValidateNameRole(fullName, role, errors, out var name);
        // Mật khẩu để trống: hệ thống tự tạo mật khẩu tạm
        if (!string.IsNullOrEmpty(password) && PasswordRules.Validate(password, e) is { } pw) errors["password"] = [pw];
        return errors.Count > 0 ? (null, errors) : (new UserInput(e!, name!, role!), errors);
    }

    public static (UserInput? Input, Dictionary<string, string[]> Errors) ValidateUpdate(string currentEmail, string? fullName, string? role)
    {
        var errors = new Dictionary<string, string[]>();
        ValidateNameRole(fullName, role, errors, out var name);
        return errors.Count > 0 ? (null, errors) : (new UserInput(currentEmail, name!, role!), errors);
    }

    private static void ValidateNameRole(string? fullName, string? role, Dictionary<string, string[]> errors, out string? name)
    {
        name = CandidateRules.NormalizeName(fullName);
        if (fullName?.Any(c => char.IsControl(c) && !char.IsWhiteSpace(c)) == true) errors["fullName"] = ["Họ tên không được chứa ký tự điều khiển."];
        if (name is null) errors["fullName"] = ["Họ tên là bắt buộc."];
        else if (name.Length > MaxNameLength) errors["fullName"] = [$"Họ tên tối đa {MaxNameLength} ký tự."];
        if (!UserRoles.IsValid(role)) errors["role"] = [$"Vai trò phải là một trong: {string.Join(", ", UserRoles.All)}."];
    }

    /// <summary>Đổi vai trò. activeAdminCount = số Admin đang hoạt động (gồm cả target nếu target là Admin).</summary>
    public static string? CanChangeRole(int actorUserId, UserSnapshot target, string newRole, int activeAdminCount)
    {
        if (target.Role == newRole) return null;
        if (target.UserId == actorUserId) return "Không tự đổi vai trò của chính mình. Nhờ một Admin khác làm.";
        if (IsLastActiveAdmin(target, activeAdminCount) && newRole != UserRoles.Admin)
            return "Đây là Admin cuối cùng đang hoạt động. Hãy cấp quyền Admin cho người khác trước.";
        return null;
    }

    public static string? CanLock(int actorUserId, UserSnapshot target, int activeAdminCount)
    {
        if (!target.IsActive) return null;   // đã khóa: không làm gì
        if (target.UserId == actorUserId) return "Không tự khóa tài khoản của chính mình.";
        if (IsLastActiveAdmin(target, activeAdminCount)) return "Không khóa Admin cuối cùng đang hoạt động.";
        return null;
    }

    private static bool IsLastActiveAdmin(UserSnapshot target, int activeAdminCount) =>
        target.Role == UserRoles.Admin && target.IsActive && activeAdminCount <= 1;

    /// <summary>
    /// Mật khẩu tạm 12 ký tự, ngẫu nhiên bằng RandomNumberGenerator (an toàn mật mã), bỏ các ký tự dễ nhầm
    /// (0/O, 1/l/I) để HR đọc qua điện thoại không sai. Luôn có chữ hoa, chữ thường và số → đạt PasswordRules.
    /// </summary>
    public static string GenerateTemporaryPassword(int length = 12)
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnpqrstuvwxyz", digits = "23456789";
        const string all = upper + lower + digits;
        if (length < PasswordRules.MinLength || length > PasswordRules.MaxLength) throw new ArgumentOutOfRangeException(nameof(length));
        var chars = new char[length];
        chars[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        for (var i = 3; i < length; i++) chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }
}
