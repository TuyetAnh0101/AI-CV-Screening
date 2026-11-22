namespace SmartCv.Core.Options;

public sealed class RegistrationOptions
{
    public const string SectionName = "Registration";
    public bool Enabled { get; set; } = false;
    public bool EmailPasswordEnabled { get; set; } = true;
    public string TermsVersion { get; set; } = "2026-11";

    public bool IsValid() => !string.IsNullOrWhiteSpace(TermsVersion)
        && TermsVersion.Length <= 40
        && TermsVersion.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}
