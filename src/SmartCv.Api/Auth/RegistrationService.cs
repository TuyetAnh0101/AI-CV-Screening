using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartCv.Api.Auditing;
using SmartCv.Core.Auth;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Email;
using SmartCv.Core.Entities;
using SmartCv.Core.Workspaces;
using SmartCv.Infrastructure.Auth;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Workspaces;

namespace SmartCv.Api.Auth;

/// <summary>
/// A56. Mỗi write SQL chạy trong transaction riêng. Không gửi SMTP trong request:
/// enqueue outbox cùng transaction với user và token.
/// </summary>
public sealed class RegistrationService(
    SmartCvDbContext db,
    IPasswordHasher<User> hasher,
    UserTokenService tokens,
    IEmailQueue queue,
    WorkspaceProvisioner provisioner,
    JwtTokenService jwt,
    AuditTrail audit,
    TimeProvider time)
{
    private static readonly TimeSpan VerifyLifetime = TimeSpan.FromHours(24);
    private static readonly RegistrationNotice Accepted = new(
        "Nếu thông tin hợp lệ, hướng dẫn xác minh sẽ được gửi tới email của bạn.");

    public Task<RegistrationNotice> RegisterAsync(ValidatedRegistration input, CancellationToken ct) =>
        RegisterCoreAsync(input, ct);

    private async Task<RegistrationNotice> RegisterCoreAsync(ValidatedRegistration input, CancellationToken ct)
    {
        // KDF luôn chạy một lần kể cả khi email đã tồn tại; không xác nhận tồn tại qua response.
        var candidate = new User { Email = input.Email };
        var passwordHash = hasher.HashPassword(candidate, input.Password);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await RegistrationEmailLock.AcquireAsync(db, input.Email, ct);
        var existing = await db.Users.SingleOrDefaultAsync(u => u.Email == input.Email, ct);
        var now = time.GetUtcNow().UtcDateTime;
        var created = existing is null;
        User target;
        if (created)
        {
            target = new User
            {
                Email = input.Email, FullName = input.FullName, PasswordHash = passwordHash,
                Role = UserRoles.Hr, SignupSource = SignupSources.Local,
                IsActive = true, MustChangePassword = false,
                EmailVerifiedAt = null, TermsAcceptedAt = now, CreatedAt = now
            };
            db.Users.Add(target);
            await db.SaveChangesAsync(ct); // Identity UserId cần cho UserTokens và EmailOutbox.
            await EnqueueVerificationAsync(target, ct);
        }
        else
        {
            target = existing!;
            if (RegistrationRules.NeedsVerification(target.IsActive, target.EmailVerifiedAt, target.SignupSource))
            {
                if (await UnderHourlyLimitAsync(target.Email, ct))
                    await EnqueueVerificationAsync(target, ct); // Mật khẩu và tên cũ KHÔNG thay đổi.
            }
            else if (target.IsActive && await UnderHourlyLimitAsync(target.Email, ct))
            {
                // Verified / provider: không tạo user mới, không đổi mật khẩu. Email gửi qua outbox.
                var template = target.SignupSource is SignupSources.Google or SignupSources.LinkedIn
                    ? EmailTemplate.RegisteredWithProvider : EmailTemplate.AccountAlreadyExists;
                await queue.EnqueueAsync(target.UserId, template,
                    new EmailTemplateModel(target.Email, target.FullName, Provider: target.SignupSource), ct);
            }
        }

        audit.Stage(db, "auth.register", target.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            credentialVerifiedUser: created ? target : null, outcome: created ? "Succeeded" : "NoChange");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        audit.ConfirmCommitted();
        return Accepted;
    }

    public async Task<RegistrationNotice> ResendAsync(string? rawEmail, CancellationToken ct)
    {
        var email = RegistrationRules.NormalizeResendEmail(rawEmail);
        if (email is null) return Accepted; // Không phân biệt email tồn tại hay không.

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await RegistrationEmailLock.AcquireAsync(db, email, ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);
        if (user is not null && RegistrationRules.NeedsVerification(user.IsActive, user.EmailVerifiedAt, user.SignupSource)
            && await UnderHourlyLimitAsync(user.Email, ct))
        {
            await EnqueueVerificationAsync(user, ct);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Accepted;
    }

    public async Task<(LoginResponse? Login, string? Error)> VerifyAsync(string? token, CancellationToken ct)
    {
        // Lookup SHA-256 trên index; không trả dữ liệu người dùng nếu token không đúng purpose.
        var lookup = await tokens.LookupAsync(token, TokenPurposes.EmailVerification, ct);
        if (lookup.Status == TokenLookupStatus.Invalid) return (null, "token_invalid");
        if (lookup.Status == TokenLookupStatus.Expired) return (null, "token_expired");

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        // UPDATE có điều kiện bảo đảm hai request không thể cùng consume một token.
        if (!await tokens.ConsumeAsync(token, TokenPurposes.EmailVerification, ct))
            return (null, "token_invalid");

        var user = await db.Users.SingleOrDefaultAsync(x => x.UserId == lookup.UserId, ct);
        if (user is null || !RegistrationRules.NeedsVerification(user.IsActive, user.EmailVerifiedAt, user.SignupSource))
            return (null, "token_invalid");

        user.EmailVerifiedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        var hadPersonal = await db.Workspaces.AnyAsync(w =>
            w.Kind == WorkspaceKinds.Personal && w.OwnerUserId == user.UserId, ct);

        // Provisioner PHẢI tham gia transaction hiện tại, không tự begin/commit lồng nhau.
        var ws = await provisioner.EnsurePersonalAsync(user, ct);
        var workspace = new WorkspaceSessionInfo(ws.WorkspaceId, ws.Name, ws.Kind, WorkspaceRoles.Owner);
        audit.Stage(db, "auth.verify_email", user.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            credentialVerifiedUser: user, workspaceId: ws.WorkspaceId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        audit.ConfirmCommitted();
        var (jwtToken, expires) = jwt.Create(user, ws.WorkspaceId);
        return (new LoginResponse
        {
            Token = jwtToken, ExpiresAt = expires,
            User = AuthMapping.ToMe(user, workspace), IsNewWorkspace = !hadPersonal
        }, null);
    }

    private async Task EnqueueVerificationAsync(User user, CancellationToken ct)
    {
        var token = await tokens.CreateAsync(user.UserId, TokenPurposes.EmailVerification, VerifyLifetime, ct);
        // Raw token chỉ vào protected payload, không được ghi ra log/audit/UserTokens.
        var action = "/verify-email?token=" + Uri.EscapeDataString(token);
        await queue.EnqueueAsync(user.UserId, EmailTemplate.VerifyEmail,
            new EmailTemplateModel(user.Email, user.FullName, ActionPath: action), ct);
    }

    private async Task<bool> UnderHourlyLimitAsync(string email, CancellationToken ct)
    {
        var earliest = time.GetUtcNow().UtcDateTime.AddHours(-1);
        var count = await db.EmailOutbox.CountAsync(x => x.ToAddress == email && x.CreatedAt >= earliest &&
            (x.Template == "VerifyEmail" || x.Template == "AccountAlreadyExists" ||
             x.Template == "RegisteredWithProvider"), ct);
        return count < 3;
    }

}
