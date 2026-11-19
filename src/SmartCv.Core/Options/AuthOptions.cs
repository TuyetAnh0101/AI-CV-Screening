namespace SmartCv.Core.Options;

/// <summary>Auth:SeedAdmin được cấu hình riêng qua User Secrets của API.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";
    public SeedAdminOptions? SeedAdmin { get; set; }
}

public sealed class SeedAdminOptions
{
    public string? Email { get; set; }
    public string? FullName { get; set; }
    public string? Password { get; set; }
}
