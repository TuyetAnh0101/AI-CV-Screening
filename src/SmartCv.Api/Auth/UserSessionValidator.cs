using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SmartCv.Core.Auth;
using SmartCv.Core.Workspaces;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Auth;

public interface ISessionStateStore
{
    Task<UserSessionState?> GetAsync(int userId, CancellationToken ct);

    /// <summary>
    /// A52: cùng một lần đọc trạng thái tài khoản và membership của <paramref name="workspaceId"/>.
    /// Mặc định bỏ qua workspace để các cài đặt/test double cũ vẫn biên dịch; DbSessionStateStore ghi đè.
    /// </summary>
    Task<UserSessionState?> GetAsync(int userId, int? workspaceId, CancellationToken ct) => GetAsync(userId, ct);
}

public sealed class DbSessionStateStore(SmartCvDbContext db) : ISessionStateStore
{
    public Task<UserSessionState?> GetAsync(int userId, CancellationToken ct) => GetAsync(userId, null, ct);

    public async Task<UserSessionState?> GetAsync(int userId, int? workspaceId, CancellationToken ct)
    {
        // Một truy vấn duy nhất: tài khoản + (nếu token có claim workspace) membership và trạng thái Active của workspace đó.
        // Users/WorkspaceMembers/Workspaces là dữ liệu toàn cục nên không bị query filter theo workspace.
        var row = await db.Users.AsNoTracking()
            .Where(u => u.UserId == userId)
            .Select(u => new
            {
                u.IsActive, u.SecurityStamp, u.Role, u.MustChangePassword, u.FullName, u.Email,
                Member = workspaceId == null ? null : db.WorkspaceMembers
                    .Where(m => m.UserId == u.UserId && m.WorkspaceId == workspaceId)
                    .Select(m => new { m.WorkspaceId, m.Workspace.Name, m.Workspace.Kind, m.Role, m.Workspace.IsActive, m.Workspace.OwnerUserId })
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var workspace = row.Member is { } m ? new WorkspaceSessionInfo(m.WorkspaceId, m.Name, m.Kind, m.Role, m.OwnerUserId) : null;
        return new UserSessionState(row.IsActive, row.SecurityStamp, row.Role, row.MustChangePassword, row.FullName, row.Email,
            workspace, row.Member?.IsActive ?? true);
    }
}

public sealed record SessionValidation(string? Error, WorkspaceSessionInfo? Workspace);

public sealed class UserSessionValidator(ISessionStateStore store, IMemoryCache cache, TimeProvider time)
{
    private sealed record CachedState(UserSessionState? State, DateTimeOffset Until);
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    public static string CacheKey(int userId) => $"smartcv:session:{userId}";
    public static void Invalidate(IMemoryCache cache, int userId) => cache.Remove(CacheKey(userId));

    public async Task<string?> ValidateAsync(ClaimsPrincipal principal, CancellationToken ct, bool requireFresh = false) =>
        (await ValidateSessionAsync(principal, ct, requireFresh)).Error;

    /// <summary>
    /// Kiểm tra phiên. Với claim smartcv_ws: workspace phải còn membership của tài khoản và đang Active (đọc từ database mỗi request),
    /// nếu không phiên bị từ chối. Token không có claim (phát hành trước A52 hoặc tài khoản chưa có workspace) vẫn hợp lệ cho các
    /// endpoint SignedIn; endpoint nghiệp vụ bị policy từ chối vì <see cref="SessionValidation.Workspace"/> là null.
    /// </summary>
    public async Task<SessionValidation> ValidateSessionAsync(ClaimsPrincipal principal, CancellationToken ct, bool requireFresh = false)
    {
        if (principal.GetUserId() is not { } id) return new("Token thiếu mã tài khoản hợp lệ.", null);
        foreach (var claim in new[] { SmartCvClaims.UserId, SmartCvClaims.Email, SmartCvClaims.Name, SmartCvClaims.Role, SmartCvClaims.Stamp, SmartCvClaims.MustChangePassword })
            if (principal.FindAll(claim).Count() != 1 || string.IsNullOrWhiteSpace(principal.FindFirstValue(claim))) return new("Token thiếu hoặc trùng thông tin tài khoản.", null);
        if (!UserRoles.IsValid(principal.FindFirstValue(SmartCvClaims.Role)) || principal.FindFirstValue(SmartCvClaims.MustChangePassword) is not ("true" or "false"))
            return new("Token chứa thông tin tài khoản không hợp lệ.", null);

        int? workspaceId = null;
        var workspaceClaims = principal.FindAll(SmartCvClaims.Workspace).ToList();
        if (workspaceClaims.Count > 1) return new(SessionRules.WorkspaceClaimInvalid, null);
        if (workspaceClaims.Count == 1)
        {
            if (!int.TryParse(workspaceClaims[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                return new(SessionRules.WorkspaceClaimInvalid, null);
            workspaceId = parsed;
        }

        var now = time.GetUtcNow();
        UserSessionState? state;
        if (requireFresh)
            state = await store.GetAsync(id, workspaceId, ct);
        else
        {
            if (!cache.TryGetValue(CacheKey(id), out CachedState? saved) || saved is null || saved.Until <= now)
            {
                saved = new(await store.GetAsync(id, ct), now.Add(CacheFor));
                cache.Set(CacheKey(id), saved, CacheFor);
            }
            state = saved.State;
        }
        if (SessionRules.Check(state, principal.FindFirstValue(SmartCvClaims.Stamp)) is { } error) return new(error, null);
        if (!requireFresh) return new(null, null);   // đường cache 30 giây không xác nhận workspace; phiên thật luôn dùng requireFresh

        // A29: the request must use current account privileges, even if a writer did not rotate the stamp.
        var current = state!;
        if (!UserRoles.IsValid(current.Role) || current.Role != principal.GetRole()
            || current.MustChangePassword is null
            || (current.MustChangePassword.Value ? "true" : "false") != principal.FindFirstValue(SmartCvClaims.MustChangePassword))
            return new("Quyền tài khoản đã thay đổi. Hãy đăng nhập lại.", null);
        if (string.IsNullOrWhiteSpace(current.FullName) || string.IsNullOrWhiteSpace(current.Email))
            return new("Thông tin tài khoản không hợp lệ.", null);

        // A52: membership/workspace lấy từ database, không tin vai trò workspace trong token (token không mang vai trò workspace).
        WorkspaceSessionInfo? workspace = null;
        if (workspaceId is { } claimed)
        {
            if (SessionRules.CheckWorkspace(current, claimed) is { } revoked) return new(revoked, null);
            workspace = current.Workspace;
        }

        // Actor is a snapshot of the current trusted database name, never a client-supplied field.
        var identity = (ClaimsIdentity)principal.Identity!;
        foreach (var type in new[] { SmartCvClaims.Name, SmartCvClaims.Email })
            foreach (var claim in identity.FindAll(type).ToList()) identity.RemoveClaim(claim);
        identity.AddClaim(new(SmartCvClaims.Name, current.FullName));
        identity.AddClaim(new(SmartCvClaims.Email, current.Email));
        return new(null, workspace);
    }
}
