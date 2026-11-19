using SmartCv.Core.Candidates;
using SmartCv.Core.Options;

namespace SmartCv.Core.Auth;

public enum SeedAdminAction { AlreadyHasAdmin, MissingConfig, Invalid, EmailTaken, Create }

// Chỉ chứa quyết định và thông tin đã chuẩn hóa; không giữ mật khẩu.
public sealed record SeedAdminDecision(SeedAdminAction Action, string Message, string? Email = null, string? FullName = null);

public static class SeedAdminPlan
{
    public static SeedAdminDecision Decide(bool hasActiveAdmin, SeedAdminOptions? seed, Func<string, bool> emailExists)
    {
        if (hasActiveAdmin) return new(SeedAdminAction.AlreadyHasAdmin, "Đã có tài khoản Admin đang hoạt động.");
        if (seed is null || string.IsNullOrWhiteSpace(seed.Email) || string.IsNullOrWhiteSpace(seed.Password))
            return new(SeedAdminAction.MissingConfig,
                "Chưa có tài khoản Admin nào. Đặt Auth:SeedAdmin:Email và Auth:SeedAdmin:Password (FullName tùy chọn) bằng User Secrets rồi chạy lại API.");

        var email = CandidateRules.NormalizeEmail(seed.Email);
        if (email is null) return new(SeedAdminAction.Invalid, "Auth:SeedAdmin:Email không đúng định dạng email hoặc vượt 254 ký tự.");
        if (PasswordRules.Validate(seed.Password, email) is { } error)
            return new(SeedAdminAction.Invalid, $"Auth:SeedAdmin:Password chưa đạt: {error}");
        var name = CandidateRules.NormalizeName(seed.FullName) ?? "Quản trị hệ thống";
        if (name.Length > 150) return new(SeedAdminAction.Invalid, "Auth:SeedAdmin:FullName tối đa 150 ký tự.");
        if (emailExists(email))
            return new(SeedAdminAction.EmailTaken,
                "Email SeedAdmin đã thuộc tài khoản khác hoặc đã khóa. Không tự nâng quyền hoặc mở khóa; dùng email khác hoặc nhờ quản trị xử lý.");
        return new(SeedAdminAction.Create, "Tạo tài khoản Admin đầu tiên.", email, name);
    }
}
