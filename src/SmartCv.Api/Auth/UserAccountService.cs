using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Data.SqlClient;
using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Auth;

/// <summary>Account writes revalidate the actor and the target under the same database-wide account lock.</summary>
public sealed class UserAccountService(SmartCvDbContext db, AccountWriteCoordinator coordinator,
    IPasswordHasher<User> hasher, IMemoryCache cache, TimeProvider time, ILogger<UserAccountService> logger)
{
    public async Task<List<UserDto>> ListAsync(HttpContext http, CancellationToken ct)
    {
        await ActorAsync(http.User, requireAdmin: true, ct);
        string? Value(string name)
        {
            var values = http.Request.Query[name];
            if (values.Count > 1) throw AccountFailure.Invalid(new() { [name] = ["Chỉ gửi trường này một lần."] });
            return values.Count == 0 ? null : values[0];
        }
        int Number(string name, int fallback, int max)
        {
            var raw = Value(name);
            if (raw is null) return fallback;
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 || number > max)
                throw AccountFailure.Invalid(new() { [name] = [$"Giá trị phải từ 1 đến {max}."] });
            return number;
        }
        var search = Value("search")?.Trim(); var role = Value("role"); var activeText = Value("isActive");
        if (search?.Length > 254) throw AccountFailure.Invalid(new() { ["search"] = ["Từ khóa tối đa 254 ký tự."] });
        if (role is not null && !UserRoles.IsValid(role)) throw AccountFailure.Invalid(new() { ["role"] = ["Vai trò phải là Admin hoặc HR."] });
        bool? active = null;
        if (activeText is not null)
        {
            if (!bool.TryParse(activeText, out var flag)) throw AccountFailure.Invalid(new() { ["isActive"] = ["Dùng true hoặc false."] });
            active = flag;
        }
        var page = Number("page", 1, int.MaxValue); var pageSize = Number("pageSize", 100, 200);
        if ((long)(page - 1) * pageSize > int.MaxValue) throw AccountFailure.Invalid(new() { ["page"] = ["Số trang quá lớn."] });
        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrEmpty(search)) { var text = search.ToLowerInvariant(); query = query.Where(u => u.Email.Contains(text) || u.FullName.ToLower().Contains(text)); }
        if (role is not null) query = query.Where(u => u.Role == role);
        if (active is { } state) query = query.Where(u => u.IsActive == state);
        var count = await query.CountAsync(ct);
        // PasswordHash is deliberately omitted from the list projection.
        var rows = await query.OrderByDescending(u => u.IsActive).ThenBy(u => u.FullName).ThenBy(u => u.UserId)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(u => new User
            {
                UserId = u.UserId, Email = u.Email, FullName = u.FullName, Role = u.Role, IsActive = u.IsActive,
                MustChangePassword = u.MustChangePassword, CreatedAt = u.CreatedAt, LastLoginAt = u.LastLoginAt, SecurityStamp = u.SecurityStamp
            }).ToListAsync(ct);
        http.Response.Headers["X-Total-Count"] = count.ToString(CultureInfo.InvariantCulture);
        http.Response.Headers["X-Page"] = page.ToString(CultureInfo.InvariantCulture);
        http.Response.Headers["X-Page-Size"] = pageSize.ToString(CultureInfo.InvariantCulture);
        return rows.Select(AuthMapping.ToDto).ToList();
    }

    public async Task<UserCredentialResponse> CreateAsync(CreateUserRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var (input, errors) = UserRules.ValidateCreate(request.Email, request.FullName, request.Role, request.Password);
        if (input is null) throw AccountFailure.Invalid(errors);
        await using var write = await coordinator.BeginAsync(db, ct);
        var actor = await ActorAsync(principal, true, ct);
        if (await db.Users.AnyAsync(u => u.Email == input.Email, ct)) throw Duplicate();
        var generated = string.IsNullOrEmpty(request.Password);
        var password = generated ? UserRules.GenerateTemporaryPassword() : request.Password!;
        var user = new User { Email = input.Email, FullName = input.FullName, Role = input.Role, IsActive = true,
            MustChangePassword = true, CreatedAt = time.GetUtcNow().UtcDateTime };
        user.PasswordHash = hasher.HashPassword(user, password);
        db.Users.Add(user);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 }) { throw Duplicate(); }
        await write.CommitAsync(ct);
        logger.LogInformation("Account action {Action} by user {ActorId} on user {TargetId} at {Utc}", "create", actor.UserId, user.UserId, time.GetUtcNow());
        return new() { User = AuthMapping.ToDto(user), TemporaryPassword = generated ? password : null };
    }

    public async Task<UserDto> UpdateAsync(int id, UpdateUserRequest request, ClaimsPrincipal principal, string? version, CancellationToken ct)
    {
        var user = await MutateAsync(id, principal, true, version, "update", async current =>
        {
            var (input, errors) = UserRules.ValidateUpdate(current.Email, request.FullName, request.Role);
            if (input is null) throw AccountFailure.Invalid(errors);
            var count = await db.Users.CountAsync(u => u.IsActive && u.Role == UserRoles.Admin, ct);
            if (UserRules.CanChangeRole(principal.GetUserId()!.Value, new(id, current.Role, current.IsActive), input.Role, count) is { } why)
                throw new AccountFailure(409, "admin_safety", why);
            if (current.FullName == input.FullName && current.Role == input.Role) return false;
            var roleChanged = current.Role != input.Role;
            current.FullName = input.FullName; current.Role = input.Role;
            if (roleChanged) current.SecurityStamp = User.NewStamp();
            return true;
        }, ct);
        return AuthMapping.ToDto(user);
    }

    public async Task<UserDto> SetActiveAsync(int id, bool active, ClaimsPrincipal principal, string? version, CancellationToken ct)
    {
        var user = await MutateAsync(id, principal, true, version, active ? "unlock" : "lock", async current =>
        {
            if (current.IsActive == active) return false;
            if (!active && UserRules.CanLock(principal.GetUserId()!.Value, new(id, current.Role, current.IsActive),
                await db.Users.CountAsync(u => u.IsActive && u.Role == UserRoles.Admin, ct)) is { } why)
                throw new AccountFailure(409, "admin_safety", why);
            current.IsActive = active; current.SecurityStamp = User.NewStamp();
            return true;
        }, ct);
        return AuthMapping.ToDto(user);
    }

    public async Task<UserCredentialResponse> ResetPasswordAsync(int id, ClaimsPrincipal principal, string? version, CancellationToken ct)
    {
        string? temporary = null;
        var user = await MutateAsync(id, principal, true, version, "reset-password", current =>
        {
            // Administrative resets are for HR. An Admin changes their own password with current-password verification.
            if (current.Role == UserRoles.Admin) throw new AccountFailure(409, "admin_password_reset_forbidden", "Admin phải tự đổi mật khẩu bằng mật khẩu hiện tại.");
            temporary = UserRules.GenerateTemporaryPassword();
            current.PasswordHash = hasher.HashPassword(current, temporary);
            current.MustChangePassword = true; current.SecurityStamp = User.NewStamp();
            return Task.FromResult(true);
        }, ct);
        return new() { User = AuthMapping.ToDto(user), TemporaryPassword = temporary };
    }

    public async Task<User> ChangePasswordAsync(ChangePasswordRequest request, ClaimsPrincipal principal, string? version, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.CurrentPassword) || request.CurrentPassword.Length > PasswordRules.MaxLength)
            throw AccountFailure.Invalid(new() { ["currentPassword"] = ["Mật khẩu hiện tại là bắt buộc và tối đa 128 ký tự."] });
        return await MutateAsync(principal.GetUserId() ?? throw SessionEnded(), principal, false, version, "change-password", current =>
        {
            if (PasswordRules.Validate(request.NewPassword, current.Email) is { } error)
                throw AccountFailure.Invalid(new() { ["newPassword"] = [error] });
            PasswordVerificationResult verified;
            try { verified = hasher.VerifyHashedPassword(current, current.PasswordHash, request.CurrentPassword); }
            catch (FormatException) { throw new AccountFailure(503, "account_unavailable", "Không xác minh được mật khẩu tài khoản."); }
            if (verified == PasswordVerificationResult.Failed) throw AccountFailure.Invalid(new() { ["currentPassword"] = ["Mật khẩu hiện tại không đúng."] });
            if (request.NewPassword == request.CurrentPassword) throw AccountFailure.Invalid(new() { ["newPassword"] = ["Mật khẩu mới phải khác mật khẩu hiện tại."] });
            current.PasswordHash = hasher.HashPassword(current, request.NewPassword!);
            current.MustChangePassword = false; current.SecurityStamp = User.NewStamp();
            return Task.FromResult(true);
        }, ct);
    }

    private async Task<User> MutateAsync(int id, ClaimsPrincipal principal, bool admin, string? expectedVersion,
        string action, Func<User, Task<bool>> change, CancellationToken ct)
    {
        if (id <= 0) throw AccountFailure.Invalid(new() { ["userId"] = ["Mã tài khoản phải là số nguyên dương."] });
        var observed = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == id, ct);
        await using var write = await coordinator.BeginAsync(db, ct);
        var actor = await ActorAsync(principal, admin, ct);
        var current = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == id, ct);
        if (observed is null || current is null) throw new AccountFailure(404, "account_not_found", "Không tìm thấy tài khoản.");
        var revision = AuthMapping.Version(current);
        if (revision != AuthMapping.Version(observed) || (expectedVersion is not null && revision != expectedVersion)) throw AccountFailure.Conflict();
        var stamp = current.SecurityStamp; var hash = current.PasswordHash; var role = current.Role;
        var originalName = current.FullName; var originalEmail = current.Email;
        var active = current.IsActive; var mustChange = current.MustChangePassword;
        if (await change(current))
        {
            if (db.Database.IsSqlServer())
            {
                // A28 login can rehash concurrently without taking this lock. The hash/stamp predicates prevent overwrites.
                var affected = await db.Users.Where(u => u.UserId == id && u.IsActive == active && u.Role == role && u.MustChangePassword == mustChange
                    && EF.Functions.Collate(u.FullName, "Latin1_General_100_BIN2") == originalName
                    && EF.Functions.Collate(u.Email, "Latin1_General_100_BIN2") == originalEmail
                    && EF.Functions.Collate(u.SecurityStamp, "Latin1_General_100_BIN2") == stamp
                    && EF.Functions.Collate(u.PasswordHash, "Latin1_General_100_BIN2") == hash)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.FullName, current.FullName).SetProperty(u => u.Role, current.Role)
                        .SetProperty(u => u.IsActive, current.IsActive).SetProperty(u => u.MustChangePassword, current.MustChangePassword)
                        .SetProperty(u => u.PasswordHash, current.PasswordHash).SetProperty(u => u.SecurityStamp, current.SecurityStamp), ct);
                if (affected != 1) throw AccountFailure.Conflict();
            }
            else
            {
                db.Users.Attach(current);
                foreach (var name in new[] { nameof(User.FullName), nameof(User.Role), nameof(User.IsActive), nameof(User.MustChangePassword), nameof(User.PasswordHash), nameof(User.SecurityStamp) })
                    db.Entry(current).Property(name).IsModified = true;
                await db.SaveChangesAsync(ct);
            }
            await write.CommitAsync(ct);
            UserSessionValidator.Invalidate(cache, id);
            logger.LogInformation("Account action {Action} by user {ActorId} on user {TargetId} at {Utc}; role changed {RoleChanged}", action, actor.UserId, id, time.GetUtcNow(), role != current.Role);
        }
        else await write.CommitAsync(ct);
        return current;
    }

    private async Task<User> ActorAsync(ClaimsPrincipal principal, bool requireAdmin, CancellationToken ct)
    {
        var id = principal.GetUserId();
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == id, ct);
        if (actor is null || !actor.IsActive || actor.SecurityStamp != principal.FindFirstValue(SmartCvClaims.Stamp)
            || actor.Role != principal.GetRole() || (actor.MustChangePassword ? "true" : "false") != principal.FindFirstValue(SmartCvClaims.MustChangePassword)) throw SessionEnded();
        if (requireAdmin && (actor.Role != UserRoles.Admin || actor.MustChangePassword)) throw new AccountFailure(403, "access_denied", "Cần quyền Admin và mật khẩu chính thức.");
        return actor;
    }
    private static AccountFailure SessionEnded() => new(401, "authentication_required", "Phiên đăng nhập không còn hiệu lực. Hãy đăng nhập lại.");
    private static AccountFailure Duplicate() => new(409, "email_conflict", "Email đã có tài khoản. Kiểm tra trạng thái tài khoản đã có.");
}