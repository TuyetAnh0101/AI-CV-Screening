using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SmartCv.Api.Auditing;
using SmartCv.Core.Auth;
using SmartCv.Core.Candidates;
using SmartCv.Core.Contracts;
using SmartCv.Core.Email;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Auth;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Auth;

/// <summary>
/// A57. Mọi thao tác ghi tài khoản đi qua AccountWriteCoordinator A30.
/// Không xác nhận email tồn tại. Token gốc chỉ nằm trong payload mã hóa của EmailOutbox.
/// </summary>
public sealed class AccountSelfServiceService(
    SmartCvDbContext db,
    AccountWriteCoordinator coordinator,
    UserTokenService tokens,
    IEmailQueue queue,
    IPasswordHasher<User> hasher,
    TimeProvider clock,
    AuditTrail audit,
    IMemoryCache sessionCache)
{
    private static readonly TimeSpan ResetLifetime = TimeSpan.FromMinutes(30);
    public static AccountNotice GenericForgotNotice { get; } =
        new("Nếu email có tài khoản hợp lệ, hướng dẫn đặt lại mật khẩu sẽ được gửi đến địa chỉ đó.");

    /// <summary>202 chung: email không tồn tại, bị khóa, sai định dạng hay đã đăng ký đều không tiết lộ trạng thái.</summary>
    public async Task ForgotAsync(string? submittedEmail, CancellationToken ct)
    {
        var normalized = submittedEmail?.Any(char.IsControl) == true
            ? null : CandidateRules.NormalizeEmail(submittedEmail);
        if (normalized is null) return;

        // Global A30 lock serializes concurrent forgot, reset, Admin security writes.
        await using var write = await coordinator.BeginAsync(db, ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Email == normalized, ct);
        if (user is null || !user.IsActive) return;
        await IssueResetLinkAsync(user, ct);
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
    }

    /// <summary>Gửi link cho chính người đang đăng nhập, chỉ nếu chưa có mật khẩu.</summary>
    public async Task RequestPasswordSetupAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        await using var write = await coordinator.BeginAsync(db, ct);
        var user = await CurrentUserAsync(principal, ct);
        if (user.HasPassword)
            throw new AccountFailure(409, "password_already_set", "Tài khoản đã có mật khẩu. Hãy dùng chức năng đổi mật khẩu.");
        await IssueResetLinkAsync(user, ct);
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
    }

    /// <summary>Trả false khi đã đạt 3 email/hour. Count được thực hiện dưới lock trong transaction.</summary>
    private async Task<bool> IssueResetLinkAsync(User user, CancellationToken ct)
    {
        var since = clock.GetUtcNow().UtcDateTime.AddHours(-1);
        var sent = await db.EmailOutbox.CountAsync(x =>
            x.UserId == user.UserId && x.Template == nameof(EmailTemplate.ResetPassword) && x.CreatedAt >= since, ct);
        if (sent >= 3) return false;

        var rawToken = await tokens.CreateAsync(user.UserId, TokenPurposes.PasswordReset, ResetLifetime, ct);
        var link = "/reset-password?token=" + Uri.EscapeDataString(rawToken);
        await queue.EnqueueAsync(user.UserId, EmailTemplate.ResetPassword,
            new EmailTemplateModel(user.Email, user.FullName, ActionPath: link,
                Provider: user.HasPassword ? null : user.SignupSource), ct);
        return true;
    }

    /// <summary>
    /// Token là bearer credential dùng một lần, đúng purpose và còn hạn.
    /// Hash, SecurityStamp, EmailVerifiedAt và email thông báo được ghi nguyên tử.
    /// </summary>
    public async Task ResetAsync(string? rawToken, string? newPassword, CancellationToken ct)
    {
        var lookup = await tokens.LookupAsync(rawToken, TokenPurposes.PasswordReset, ct);
        if (lookup.Status == TokenLookupStatus.Invalid)
            throw new AccountFailure(400, "token_invalid", "Liên kết không hợp lệ hoặc đã được sử dụng.");
        if (lookup.Status == TokenLookupStatus.Expired)
            throw new AccountFailure(400, "token_expired", "Liên kết đặt lại mật khẩu đã hết hạn.");

        // Validate inside transaction against the email on the authoritative user record.
        await using var write = await coordinator.BeginAsync(db, ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == lookup.UserId, ct);
        if (user is null || !user.IsActive)
            throw new AccountFailure(400, "token_invalid", "Liên kết không hợp lệ hoặc đã được sử dụng.");
        if (PasswordRules.Validate(newPassword, user.Email) is { } validation)
            throw AccountFailure.Invalid(new() { ["newPassword"] = [validation] });
        if (user.HasPassword)
        {
            PasswordVerificationResult oldVerified;
            try { oldVerified = hasher.VerifyHashedPassword(user, user.PasswordHash, newPassword!); }
            catch (FormatException)
            { throw new AccountFailure(503, "account_unavailable", "Không xác minh được trạng thái mật khẩu."); }
            if (oldVerified != PasswordVerificationResult.Failed)
                throw AccountFailure.Invalid(new() { ["newPassword"] = ["Mật khẩu mới phải khác mật khẩu hiện tại."] });
        }

        // A56 ExecuteUpdate is atomic and checks token expiry + not consumed.
        if (!await tokens.ConsumeAsync(rawToken, TokenPurposes.PasswordReset, ct))
            throw new AccountFailure(400, "token_invalid", "Liên kết không hợp lệ hoặc đã được sử dụng.");

        var now = clock.GetUtcNow().UtcDateTime;
        var passwordHash = hasher.HashPassword(user, newPassword!);
        var securityStamp = User.NewStamp();
        if (db.Database.IsSqlServer())
        {
            var changed = await db.Users.Where(x => x.UserId == user.UserId && x.IsActive &&
                    x.SecurityStamp == user.SecurityStamp && x.PasswordHash == user.PasswordHash)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.PasswordHash, passwordHash)
                    .SetProperty(x => x.SecurityStamp, securityStamp)
                    .SetProperty(x => x.MustChangePassword, false)
                    .SetProperty(x => x.EmailVerifiedAt, x => x.EmailVerifiedAt ?? now), ct);
            if (changed != 1) throw AccountFailure.Conflict();
        }
        else
        {
            // Only for EF InMemory unit tests; production uses SQL Server.
            db.Users.Attach(user);
            user.PasswordHash = passwordHash;
            user.SecurityStamp = securityStamp;
            user.MustChangePassword = false;
            user.EmailVerifiedAt ??= now;
            foreach (var field in new[] { nameof(User.PasswordHash), nameof(User.SecurityStamp),
                nameof(User.MustChangePassword), nameof(User.EmailVerifiedAt) })
                db.Entry(user).Property(field).IsModified = true;
        }

        await queue.EnqueueAsync(user.UserId, EmailTemplate.PasswordChanged,
            new EmailTemplateModel(user.Email, user.FullName), ct);
        audit.Stage(db, "auth.reset_password", user.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            credentialVerifiedUser: user);
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        audit.ConfirmCommitted();
        UserSessionValidator.Invalidate(sessionCache, user.UserId);
    }

    public async Task<ProfileDto> GetProfileAsync(ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var user = await CurrentUserAsync(principal, ct);
        return await ToProfileAsync(user, http, ct);
    }

    public async Task<ProfileDto> UpdateProfileAsync(ClaimsPrincipal principal, HttpContext http,
        UpdateMyProfileRequest request, string? expectedVersion, CancellationToken ct)
    {
        var normalized = CandidateRules.NormalizeName(request.FullName);
        if (request.FullName?.Any(c => char.IsControl(c) && !char.IsWhiteSpace(c)) == true ||
            string.IsNullOrEmpty(normalized) || normalized.Length > UserRules.MaxNameLength)
            throw AccountFailure.Invalid(new() { ["fullName"] = ["Họ tên là bắt buộc, tối đa 150 ký tự và không có ký tự điều khiển."] });

        await using var write = await coordinator.BeginAsync(db, ct);
        var current = await CurrentUserAsync(principal, ct);
        if (expectedVersion is not null && AuthMapping.Version(current) != expectedVersion)
            throw AccountFailure.Conflict();

        if (current.FullName != normalized)
        {
            if (db.Database.IsSqlServer())
            {
                var changed = await db.Users.Where(u => u.UserId == current.UserId && u.IsActive &&
                        u.SecurityStamp == current.SecurityStamp && u.FullName == current.FullName)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.FullName, normalized), ct);
                if (changed != 1) throw AccountFailure.Conflict();
            }
            else
            {
                db.Users.Attach(current);
                current.FullName = normalized;
                db.Entry(current).Property(x => x.FullName).IsModified = true;
            }
            current.FullName = normalized;
            audit.Stage(db, "account.profile_updated", current.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), principal);
        }
        else
        {
            audit.Stage(db, "account.profile_updated", current.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                principal, outcome: "NoChange");
        }
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        audit.ConfirmCommitted();
        UserSessionValidator.Invalidate(sessionCache, current.UserId);
        return await ToProfileAsync(current, http, ct);
    }

    private async Task<User> CurrentUserAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = principal.GetUserId();
        if (id is not > 0)
            throw SessionEnded();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == id, ct);
        if (user is null || !user.IsActive ||
            user.SecurityStamp != principal.FindFirstValue(SmartCvClaims.Stamp) ||
            user.Role != principal.GetRole() ||
            (user.MustChangePassword ? "true" : "false") != principal.FindFirstValue(SmartCvClaims.MustChangePassword))
            throw SessionEnded();
        return user;
    }

    private async Task<ProfileDto> ToProfileAsync(User user, HttpContext http, CancellationToken ct)
    {
        var linked = await db.UserExternalLogins.AsNoTracking()
            .Where(l => l.UserId == user.UserId)
            .OrderBy(l => l.LinkedAt)
            .Select(l => new LinkedLoginDto(l.Provider, l.EmailAtLink, l.LinkedAt, l.LastUsedAt))
            .ToListAsync(ct);
        var ws = AuthMapping.ToMe(user, HttpWorkspaceContext.Get(http)).Workspace;
        static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return new ProfileDto(user.UserId, user.FullName, user.Email, user.EmailVerifiedAt is not null,
            user.HasPassword, user.Role, user.SignupSource, Utc(user.CreatedAt),
            user.LastLoginAt is null ? null : Utc(user.LastLoginAt.Value), ws,
            linked.Select(l => l with { LinkedAt = Utc(l.LinkedAt), LastUsedAt = l.LastUsedAt is null ? null : Utc(l.LastUsedAt.Value) }).ToArray());
    }

    private static AccountFailure SessionEnded() => new(401, "authentication_required", "Phiên đăng nhập không còn hiệu lực.");
}
