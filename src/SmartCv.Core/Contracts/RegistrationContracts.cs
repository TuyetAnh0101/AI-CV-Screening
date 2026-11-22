namespace SmartCv.Core.Contracts;

public sealed class RegisterRequest
{
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? Password { get; set; }
    public bool AcceptTerms { get; set; }
}

public sealed class VerifyEmailRequest
{
    public string? Token { get; set; }
}

public sealed class ResendVerificationRequest
{
    public string? Email { get; set; }
}

public sealed record RegistrationNotice(string Message);
public sealed record PublicProviderSetting(string Name, bool Enabled);
public sealed record PublicPasswordRules(int MinLength, int MaxLength, bool RequiresLetter, bool RequiresDigit);
public sealed record RegistrationSettingsResponse(bool RegistrationEnabled, bool EmailPasswordEnabled,
    IReadOnlyList<PublicProviderSetting> Providers, PublicPasswordRules PasswordRules, string TermsVersion);
