using SmartCv.Core.Candidates;

namespace SmartCv.Core.Auth;

/// <summary>Validation thuần, không cho request tự chỉ định Role hoặc SignupSource.</summary>
public sealed record ValidatedRegistration(string FullName, string Email, string Password);

public static class RegistrationRules
{
    public const int MinNameLength = 2;
    public const int MaxNameLength = 100;

    public static (ValidatedRegistration? Value, Dictionary<string, string[]> Errors) Validate(
        string? fullName, string? email, string? password, bool acceptTerms)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = CandidateRules.NormalizeName(fullName);
        if (name is null || name.Length < MinNameLength || name.Length > MaxNameLength ||
            fullName?.Any(char.IsControl) == true)
            errors["fullName"] = [$"Họ tên cần {MinNameLength}–{MaxNameLength} ký tự hợp lệ, không chứa ký tự điều khiển."];

        var normalized = email?.Any(char.IsControl) == true ? null : CandidateRules.NormalizeEmail(email);
        if (normalized is null)
            errors["email"] = ["Email không hợp lệ hoặc vượt quá 254 ký tự."];

        if (PasswordRules.Validate(password, normalized) is { } passwordError)
            errors["password"] = [passwordError];

        if (!acceptTerms) errors["acceptTerms"] = ["Cần đồng ý điều khoản sử dụng."];
        return errors.Count == 0
            ? (new(name!, normalized!, password!), errors)
            : (null, errors);
    }

    public static string? NormalizeResendEmail(string? email) =>
        email?.Any(char.IsControl) == true ? null : CandidateRules.NormalizeEmail(email);

    public static bool NeedsVerification(bool isActive, DateTime? emailVerifiedAt, string signupSource) =>
        isActive && emailVerifiedAt is null && signupSource == SmartCv.Core.Constants.SignupSources.Local;

    public static bool CanProvisionOnLogin(string signupSource, DateTime? emailVerifiedAt, bool isActive) =>
        isActive && emailVerifiedAt is not null && signupSource != SmartCv.Core.Constants.SignupSources.Admin;
}
