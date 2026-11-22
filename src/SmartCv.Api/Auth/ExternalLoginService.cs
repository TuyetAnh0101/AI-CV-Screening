using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Api.Auditing;
using SmartCv.Core.Auth;
using SmartCv.Core.Candidates;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Email;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Auth;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Workspaces;

namespace SmartCv.Api.Auth;

public sealed record ExternalCompletion(string RedirectUrl, string? FailureCode);
public sealed record ExchangeLoginResult(LoginResponse Login, string ReturnUrl);
public sealed record ExternalStartUrl(string StartUrl);

/// <summary>
/// A58. No implicit email linking. All mutations use A30 global SQL applock + transaction.
/// Only OIDC-validated principal from the short-lived External cookie is accepted here.
/// </summary>
public sealed class ExternalLoginService(
    SmartCvDbContext db,
    AccountWriteCoordinator coordinator,
    UserTokenService tokens,
    IEmailQueue emails,
    WorkspaceProvisioner workspaces,
    JwtTokenService jwt,
    AuditTrail audit,
    TimeProvider clock,
    IOptions<RegistrationOptions> registration,
    IConfiguration config)
{
    private const string CodePurpose = ExternalProviders.ExternalLoginCode;
    private const string LinkPurpose = ExternalProviders.ExternalLinkTicket;
    public const string FlowIntent = "a58.intent";
    public const string FlowTicket = "a58.ticket";
    public const string FlowReturn = "a58.return";
    public const string FlowProvider = "a58.provider";

    public bool Enabled => config.GetValue<bool>("Authentication:Google:Enabled");

    public static string WebLoginError(IConfiguration cfg, string code) =>
        WebUrl(cfg, "/login?external_error=" + Uri.EscapeDataString(CleanError(code)));

    private static string CleanError(string? code) => code switch
    {
        "cancelled" or "provider_error" or "email_unverified" or "link_required" or "account_locked" or
        "admin_not_allowed" or "registration_disabled" or "already_linked" or "token_invalid" or "token_expired" or
        "last_login_method" => code!,
        _ => "provider_error"
    };

    private static string WebUrl(IConfiguration cfg, string path)
    {
        var raw = cfg["App:WebBaseUrl"];
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var root) ||
            (root.Scheme != Uri.UriSchemeHttps && !(root.IsLoopback && root.Scheme == Uri.UriSchemeHttp)) ||
            !string.IsNullOrEmpty(root.UserInfo) || !string.IsNullOrEmpty(root.Query) || !string.IsNullOrEmpty(root.Fragment))
            throw new InvalidOperationException("App:WebBaseUrl must be HTTPS (localhost HTTP only).");
        return new Uri(root, path).AbsoluteUri;
    }

    private string ErrorUrl(string intent, string code) => WebUrl(config,
        (intent == "link" ? "/account?external_error=" : "/login?external_error=") + Uri.EscapeDataString(CleanError(code)));

    public async Task<string> CreateLinkStartUrlAsync(ClaimsPrincipal actor, string provider, CancellationToken ct)
    {
        if (!Enabled || provider != ExternalProviders.Google)
            throw new AccountFailure(404, "provider_disabled", "Google chưa được bật.");
        await using var write = await coordinator.BeginAsync(db, ct);
        var user = await AuthenticatedAsync(actor, ct);
        if (user.Role == UserRoles.Admin) throw new AccountFailure(403, "admin_not_allowed", "Admin dùng mật khẩu.");
        if (user.MustChangePassword) throw new AccountFailure(403, "password_change_required", "Cần đổi mật khẩu trước.");
        if (await db.UserExternalLogins.AnyAsync(x => x.UserId == user.UserId && x.Provider == provider, ct))
            throw new AccountFailure(409, "already_linked", "Google đã được liên kết.");
        var ticket = await tokens.CreateAsync(user.UserId, LinkPurpose, TimeSpan.FromMinutes(2), ct);
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        // Root relative URL: B55 must prepend the configured API origin, not the Web origin.
        return "/api/auth/external/google/start?intent=link&ticket=" + Uri.EscapeDataString(ticket);
    }

    public async Task<bool> IsValidLinkTicketAsync(string? ticket, CancellationToken ct)
    {
        if (UserTokenService.HashToken(ticket) is null) return false;
        var value = await tokens.LookupAsync(ticket, LinkPurpose, ct);
        if (value.Status != TokenLookupStatus.Valid) return false;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == value.UserId, ct);
        return user is { IsActive: true, Role: not UserRoles.Admin, MustChangePassword: false };
    }

    /// <summary>Pure decision + DB snapshots. Also called INSIDE write transaction by CompleteAsync.</summary>
    public async Task<ExternalDecision> DecideAsync(string provider, ClaimsPrincipal principal,
        string intent, int? ticketUserId, CancellationToken ct)
    {
        var identity = ExternalIdentity.FromClaims(provider, principal);
        if (!ExternalProviders.IsSupported(provider) || string.IsNullOrWhiteSpace(identity.Subject))
            return ExternalDecision.Deny("provider_error");
        var normalized = identity.Email is null || identity.Email.Any(char.IsControl)
            ? null : CandidateRules.NormalizeEmail(identity.Email);
        var linked = await db.UserExternalLogins.AsNoTracking()
            .Where(x => x.Provider == provider && x.ProviderKey == identity.Subject)
            .Select(x => new { x.UserId, x.User.IsActive, x.User.Role }).SingleOrDefaultAsync(ct);
        var ownerId = normalized is null ? null : (int?)await db.Users.AsNoTracking()
            .Where(x => x.Email == normalized).Select(x => x.UserId).FirstOrDefaultAsync(ct);
        if (ownerId == 0) ownerId = null;
        User? target = null;
        var hasProvider = false;
        if (intent == "link" && ticketUserId is { } id)
        {
            target = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == id, ct);
            hasProvider = await db.UserExternalLogins.AnyAsync(x => x.UserId == id && x.Provider == provider, ct);
        }
        var answer = ExternalDecisionRules.Decide(identity with { Email = normalized }, intent, ticketUserId,
            linked?.UserId, linked?.IsActive == true, linked?.Role == UserRoles.Admin,
            ownerId, registration.Value.Enabled, target?.IsActive == true,
            target?.Role == UserRoles.Admin, hasProvider);
        return normalized is null && intent == "login" ? ExternalDecision.Deny("email_unverified") : answer;
    }

    public async Task<ExternalCompletion> CompleteAsync(ClaimsPrincipal external, string provider,
        string intent, string? rawTicket, string? returnUrl, CancellationToken ct)
    {
        if (!Enabled || provider != ExternalProviders.Google)
            return new(ErrorUrl(intent, "provider_error"), "provider_error");
        if (intent is not ("login" or "link"))
            return new(ErrorUrl("login", "provider_error"), "provider_error");
        var identity = ExternalIdentity.FromClaims(provider, external);
        if (string.IsNullOrWhiteSpace(identity.Subject) || identity.Subject.Length > 200)
            return new(ErrorUrl(intent, "provider_error"), "provider_error");

        var safeReturn = ExternalProviders.SafeReturnUrl(returnUrl);
        await using var write = await coordinator.BeginAsync(db, ct);
        int? ticketUserId = null;
        if (intent == "link")
        {
            var ticket = await tokens.LookupAsync(rawTicket, LinkPurpose, ct);
            if (ticket.Status != TokenLookupStatus.Valid)
                return await DenyAsync(write, intent, ticket.Status == TokenLookupStatus.Expired ? "token_expired" : "token_invalid", ct);
            ticketUserId = ticket.UserId;
        }
        else if (identity.Email is { Length: > 0 } email && !email.Any(char.IsControl))
        {
            var normalized = CandidateRules.NormalizeEmail(email);
            if (normalized is not null && db.Database.IsSqlServer())
                // Serialize against A56 email registration for first-time Google accounts.
                await RegistrationEmailLock.AcquireAsync(db, normalized, ct);
        }

        var decision = await DecideAsync(provider, external, intent, ticketUserId, ct);
        if (decision.Action == ExternalAction.Reject)
            return await DenyAsync(write, intent, decision.ErrorCode ?? "provider_error", ct);

        var now = clock.GetUtcNow().UtcDateTime;
        if (decision.Action == ExternalAction.Link)
        {
            if (!await tokens.ConsumeAsync(rawTicket, LinkPurpose, ct))
                return await DenyAsync(write, intent, "token_invalid", ct);
            var owner = await db.Users.SingleAsync(x => x.UserId == ticketUserId, ct);
            db.UserExternalLogins.Add(new UserExternalLogin
            {
                UserId = owner.UserId, Provider = provider, ProviderKey = identity.Subject,
                EmailAtLink = identity.Email is null ? null : CandidateRules.NormalizeEmail(identity.Email), LinkedAt = now
            });
            await emails.EnqueueAsync(owner.UserId, EmailTemplate.ExternalLoginLinked,
                new EmailTemplateModel(owner.Email, owner.FullName, Provider: "Google"), ct);
            audit.Stage(db, "auth.external_link", owner.UserId.ToString(), credentialVerifiedUser: owner);
            await db.SaveChangesAsync(ct);
            await write.CommitAsync(ct);
            audit.ConfirmCommitted();
            return new(WebUrl(config, "/account?linked=google"), null);
        }

        User user;
        bool created;
        if (decision.Action == ExternalAction.Register)
        {
            var email = CandidateRules.NormalizeEmail(identity.Email);
            if (email is null) return await DenyAsync(write, intent, "email_unverified", ct);
            var clean = SmartCv.Core.Candidates.CandidateRules.NormalizeName(identity.FullName);
            var name = string.IsNullOrWhiteSpace(clean) ? "Người dùng Google" : clean;
            if (name.Length > 150) name = name[..150].TrimEnd();
            user = new User
            {
                Email = email, FullName = name, PasswordHash = "", Role = UserRoles.Hr,
                IsActive = true, MustChangePassword = false, SignupSource = SignupSources.Google,
                EmailVerifiedAt = now, CreatedAt = now
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            db.UserExternalLogins.Add(new UserExternalLogin
            {
                UserId = user.UserId, Provider = provider, ProviderKey = identity.Subject,
                EmailAtLink = email, LinkedAt = now
            });
            // Provision workspace in SAME transaction.
            await workspaces.EnsurePersonalAsync(user, ct);
            await emails.EnqueueAsync(user.UserId, EmailTemplate.RegisteredWithProvider,
                new EmailTemplateModel(user.Email, user.FullName, Provider: "Google"), ct);
            created = true;
        }
        else
        {
            user = await db.Users.SingleAsync(x => x.UserId == decision.UserId, ct);
            created = false;
        }

        var rawCode = await tokens.CreateAsync(user.UserId, CodePurpose, TimeSpan.FromSeconds(60), ct);
        var hash = UserTokenService.HashToken(rawCode)!;
        var inserted = db.ChangeTracker.Entries<UserToken>().Single(x => x.State == EntityState.Added && x.Entity.TokenHash == hash);
        inserted.Entity.Payload = safeReturn; // returnUrl only. No identity info, password or provider token.
        audit.Stage(db, created ? "auth.external_register" : "auth.external_login", user.UserId.ToString(),
            credentialVerifiedUser: user);
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        audit.ConfirmCommitted();
        var fragment = "/auth/callback#code=" + Uri.EscapeDataString(rawCode)
            + "&returnUrl=" + Uri.EscapeDataString(safeReturn);
        return new(WebUrl(config, fragment), null);
    }

    private async Task<ExternalCompletion> DenyAsync(AccountWriteCoordinator.WriteScope write, string intent, string error, CancellationToken ct)
    {
        var row = audit.Stage(db, "auth.external_rejected", null, outcome: "Denied");
        row.FailureCode = error;
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        audit.ConfirmCommitted();
        // Persist only the safe failure code, never an email, provider subject or credential.
        return new(ErrorUrl(intent, error), error);
    }

    public async Task<ExchangeLoginResult> ExchangeAsync(string? rawCode, CancellationToken ct)
    {
        var lookup = await tokens.LookupAsync(rawCode, CodePurpose, ct);
        if (lookup.Status != TokenLookupStatus.Valid)
            throw new AccountFailure(400, lookup.Status == TokenLookupStatus.Expired ? "token_expired" : "token_invalid", "Mã đăng nhập không hợp lệ hoặc đã hết hạn.");
        await using var write = await coordinator.BeginAsync(db, ct);
        var user = await db.Users.SingleOrDefaultAsync(x => x.UserId == lookup.UserId, ct);
        if (user is null || !user.IsActive || user.Role == UserRoles.Admin)
            throw new AccountFailure(400, "token_invalid", "Mã đăng nhập không hợp lệ.");
        if (!await tokens.ConsumeAsync(rawCode, CodePurpose, ct))
            throw new AccountFailure(400, "token_invalid", "Mã đã được sử dụng.");
        var code = await db.UserTokens.AsNoTracking().SingleAsync(x => x.UserTokenId == lookup.TokenId, ct);
        var safeReturn = ExternalProviders.SafeReturnUrl(code.Payload);
        var now = clock.GetUtcNow().UtcDateTime;
        user.LastLoginAt = now;
        var linked = await db.UserExternalLogins.SingleOrDefaultAsync(x => x.UserId == user.UserId && x.Provider == ExternalProviders.Google, ct);
        if (linked is not null) linked.LastUsedAt = now;
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        var workspace = await WorkspaceSelection.ResolveAsync(db, user.UserId, ct);
        var (accessToken, expires) = jwt.Create(user, workspace?.Id);
        return new(new LoginResponse
        {
            Token = accessToken, ExpiresAt = expires,
            User = AuthMapping.ToMe(user, workspace)
        }, safeReturn);
    }

    public async Task<IReadOnlyList<LinkedLoginDto>> GetLoginsAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var user = await AuthenticatedAsync(principal, ct);
        var list = await db.UserExternalLogins.AsNoTracking().Where(x => x.UserId == user.UserId)
            .OrderBy(x => x.LinkedAt)
            .Select(x => new LinkedLoginDto(x.Provider, x.EmailAtLink, x.LinkedAt, x.LastUsedAt))
            .ToListAsync(ct);
        return list;
    }

    public async Task UnlinkAsync(ClaimsPrincipal principal, string provider, CancellationToken ct)
    {
        if (!ExternalProviders.IsSupported(provider)) throw new AccountFailure(404, "provider_disabled", "Provider không hỗ trợ.");
        await using var write = await coordinator.BeginAsync(db, ct);
        var user = await AuthenticatedAsync(principal, ct);
        if (user.Role == UserRoles.Admin) throw new AccountFailure(403, "admin_not_allowed", "Admin dùng mật khẩu.");
        var linked = await db.UserExternalLogins.SingleOrDefaultAsync(x => x.UserId == user.UserId && x.Provider == provider, ct);
        if (linked is null) throw new AccountFailure(404, "link_not_found", "Chưa liên kết phương thức này.");
        var otherMethods = await db.UserExternalLogins.CountAsync(x => x.UserId == user.UserId && x.Provider != provider, ct);
        if (ExternalUnlinkRules.Check(user.HasPassword, otherMethods) is { } blocked)
            throw new AccountFailure(409, blocked, "Hãy đặt mật khẩu trước khi hủy liên kết.");
        db.UserExternalLogins.Remove(linked);
        await emails.EnqueueAsync(user.UserId, EmailTemplate.ExternalLoginUnlinked,
            new EmailTemplateModel(user.Email, user.FullName, Provider: "Google"), ct);
        audit.Stage(db, "auth.external_unlink", user.UserId.ToString(), principal);
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        audit.ConfirmCommitted();
        // Do NOT rotate SecurityStamp. Existing SCApp JWT sessions remain valid.
    }

    private async Task<User> AuthenticatedAsync(ClaimsPrincipal actor, CancellationToken ct)
    {
        var id = actor.GetUserId();
        var user = await db.Users.SingleOrDefaultAsync(u => u.UserId == id, ct);
        if (user is null || !user.IsActive || user.SecurityStamp != actor.FindFirstValue(SmartCvClaims.Stamp)
            || user.Role != actor.GetRole() || (user.MustChangePassword ? "true" : "false") != actor.FindFirstValue(SmartCvClaims.MustChangePassword))
            throw new AccountFailure(401, "authentication_required", "Phiên đăng nhập đã hết hạn.");
        return user;
    }
}
