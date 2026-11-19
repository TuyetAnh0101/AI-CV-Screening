using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SmartCv.Core.Auth;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Auth;

public interface ISessionStateStore
{
    Task<UserSessionState?> GetAsync(int userId, CancellationToken ct);
}

public sealed class DbSessionStateStore(SmartCvDbContext db) : ISessionStateStore
{
    public Task<UserSessionState?> GetAsync(int userId, CancellationToken ct) => db.Users.AsNoTracking()
        .Where(u => u.UserId == userId).Select(u => new UserSessionState(u.IsActive, u.SecurityStamp)).FirstOrDefaultAsync(ct);
}

public sealed class UserSessionValidator(ISessionStateStore store, IMemoryCache cache, TimeProvider time)
{
    private sealed record CachedState(UserSessionState? State, DateTimeOffset Until);
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    public static string CacheKey(int userId) => $"smartcv:session:{userId}";
    public static void Invalidate(IMemoryCache cache, int userId) => cache.Remove(CacheKey(userId));

    public async Task<string?> ValidateAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (principal.GetUserId() is not { } id) return "Token thiếu mã tài khoản hợp lệ.";
        foreach (var claim in new[] { SmartCvClaims.UserId, SmartCvClaims.Email, SmartCvClaims.Name, SmartCvClaims.Role, SmartCvClaims.Stamp, SmartCvClaims.MustChangePassword })
            if (principal.FindAll(claim).Count() != 1 || string.IsNullOrWhiteSpace(principal.FindFirstValue(claim))) return "Token thiếu hoặc trùng thông tin tài khoản.";
        if (!UserRoles.IsValid(principal.FindFirstValue(SmartCvClaims.Role)) || principal.FindFirstValue(SmartCvClaims.MustChangePassword) is not ("true" or "false"))
            return "Token chứa thông tin tài khoản không hợp lệ.";

        var now = time.GetUtcNow();
        if (!cache.TryGetValue(CacheKey(id), out CachedState? saved) || saved is null || saved.Until <= now)
        {
            var state = await store.GetAsync(id, ct);
            saved = new(state, now.Add(CacheFor));
            cache.Set(CacheKey(id), saved, CacheFor);
        }
        return SessionRules.Check(saved.State, principal.FindFirstValue(SmartCvClaims.Stamp));
    }
}
