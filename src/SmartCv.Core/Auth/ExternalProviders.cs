using System.Security.Claims;

namespace SmartCv.Core.Auth;

/// <summary>A58: nhà cung cấp và dữ liệu đã được OIDC handler xác minh chữ ký/nonce.</summary>
public static class ExternalProviders
{
    public const string Google = "google";
    public const string ExternalLoginCode = "ExternalLoginCode";
    public const string ExternalLinkTicket = "ExternalLinkTicket";
    public static bool IsSupported(string? provider) => provider == Google;

    /// <summary>Chỉ đường dẫn nội bộ; tuyệt đối không tạo open redirect.</summary>
    public static string SafeReturnUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "/dashboard";
        var text = value.Trim();
        if (text.Length > 500 || !text.StartsWith('/') || text.StartsWith("//", StringComparison.Ordinal)
            || text.StartsWith("/\\", StringComparison.Ordinal) || text.Contains('\\')
            || text.Any(char.IsControl) || text.Split('?', '#')[0].Contains(':')) return "/dashboard";
        // Reject encoded separator tricks before a Web navigation decodes the value.
        string decoded;
        try { decoded = Uri.UnescapeDataString(text); }
        catch (UriFormatException) { return "/dashboard"; }
        if (decoded.StartsWith("//", StringComparison.Ordinal) || decoded.StartsWith("/\\", StringComparison.Ordinal)
            || decoded.Contains('\\') || decoded.Any(char.IsControl) || decoded.Split('?', '#')[0].Contains(':'))
            return "/dashboard";
        var path = text.Split('?', '#')[0].TrimEnd('/');
        if (path.Length == 0 || path.Equals("/login", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/auth/callback", StringComparison.OrdinalIgnoreCase)) return "/dashboard";
        return text;
    }
}

public sealed record ExternalIdentity(string Provider, string Subject, string? Email, bool EmailVerified, string FullName)
{
    public static ExternalIdentity FromClaims(string provider, ClaimsPrincipal principal)
    {
        var subject = principal.FindFirst("sub")?.Value ?? "";
        // No email_verified claim is NOT equivalent to verified.
        var verified = string.Equals(principal.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        return new ExternalIdentity(provider, subject, principal.FindFirst("email")?.Value, verified,
            principal.FindFirst("name")?.Value ?? "");
    }
}

public enum ExternalAction { Login, Register, Link, Reject }
public sealed record ExternalDecision(ExternalAction Action, string? ErrorCode = null, int? UserId = null)
{
    public static ExternalDecision Deny(string code) => new(ExternalAction.Reject, code);
}

/// <summary>Bảng quyết định 8.4: pure function; SQL reads/writes are in the API service.</summary>
public static class ExternalDecisionRules
{
    public static ExternalDecision Decide(ExternalIdentity identity, string intent, int? ticketUserId,
        int? linkedUserId, bool linkedActive, bool linkedIsAdmin,
        int? emailOwnerUserId, bool registrationEnabled, bool targetActive = true,
        bool targetIsAdmin = false, bool targetHasProvider = false)
    {
        if (!ExternalProviders.IsSupported(identity.Provider) || string.IsNullOrWhiteSpace(identity.Subject)
            || identity.Subject.Length > 200 || identity.Subject.Any(char.IsControl))
            return ExternalDecision.Deny("provider_error");
        if (intent == "link")
        {
            if (ticketUserId is not > 0) return ExternalDecision.Deny("token_invalid");
            if (!targetActive) return ExternalDecision.Deny("account_locked");
            if (targetIsAdmin) return ExternalDecision.Deny("admin_not_allowed");
            if (linkedUserId is { } other && other != ticketUserId) return ExternalDecision.Deny("already_linked");
            if (targetHasProvider) return ExternalDecision.Deny("already_linked");
            return new ExternalDecision(ExternalAction.Link, UserId: ticketUserId);
        }
        if (intent != "login") return ExternalDecision.Deny("provider_error");
        if (!identity.EmailVerified || string.IsNullOrWhiteSpace(identity.Email))
            return ExternalDecision.Deny("email_unverified");
        if (linkedUserId is { } linked)
        {
            if (!linkedActive) return ExternalDecision.Deny("account_locked");
            if (linkedIsAdmin) return ExternalDecision.Deny("admin_not_allowed");
            return new ExternalDecision(ExternalAction.Login, UserId: linked);
        }
        if (emailOwnerUserId is not null) return ExternalDecision.Deny("link_required");
        return registrationEnabled ? new(ExternalAction.Register) : ExternalDecision.Deny("registration_disabled");
    }
}

/// <summary>Must leave at least one login method before unlinking a provider.</summary>
public static class ExternalUnlinkRules
{
    public static string? Check(bool hasPassword, int otherLinkedMethods) =>
        hasPassword || otherLinkedMethods > 0 ? null : "last_login_method";
}
