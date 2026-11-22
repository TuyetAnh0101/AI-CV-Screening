using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Auth;

public enum TokenLookupStatus { Invalid, Expired, Valid }
public sealed record TokenLookup(TokenLookupStatus Status, long TokenId, int UserId);

/// <summary>
/// Token gốc có 32 byte CSPRNG, chỉ tồn tại ở bộ nhớ request và trong EmailOutbox.ProtectedPayload.
/// UserTokens chỉ lưu SHA-256 (64 ký tự hex). Caller quản lý transaction + SaveChanges.
/// </summary>
public sealed class UserTokenService(SmartCvDbContext db, TimeProvider time)
{
    public static string CreateRandomToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string? HashToken(string? token)
    {
        if (token is null || token.Length != 43 ||
            token.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')) return null;
        try
        {
            var bytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=");
            if (bytes.Length != 32 ||
                !string.Equals(token, Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_'),
                    StringComparison.Ordinal)) return null;
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        catch (FormatException) { return null; }
    }

    /// <summary>Thu hồi token cùng purpose trước khi tạo mới. Phải nằm trong transaction đang mở.</summary>
    public async Task<string> CreateAsync(int userId, string purpose, TimeSpan lifetime, CancellationToken ct)
    {
        if (userId <= 0 || string.IsNullOrWhiteSpace(purpose) || purpose.Length > 30 || lifetime <= TimeSpan.Zero)
            throw new ArgumentException("Token arguments are invalid.");
        if (db.Database.IsSqlServer() && db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Token issuance requires an existing business transaction.");

        var now = time.GetUtcNow().UtcDateTime;
        await db.UserTokens.Where(x => x.UserId == userId && x.Purpose == purpose && x.ConsumedAt == null)
            .ExecuteUpdateAsync(up => up.SetProperty(x => x.ConsumedAt, now), ct);

        var token = CreateRandomToken();
        db.UserTokens.Add(new UserToken
        {
            UserId = userId, Purpose = purpose, TokenHash = HashToken(token)!,
            CreatedAt = now, ExpiresAt = now.Add(lifetime)
        });
        return token;
    }

    public async Task<TokenLookup> LookupAsync(string? token, string purpose, CancellationToken ct)
    {
        var hash = HashToken(token);
        if (hash is null) return new(TokenLookupStatus.Invalid, 0, 0);
        var row = await db.UserTokens.AsNoTracking()
            .Where(x => x.TokenHash == hash && x.Purpose == purpose)
            .Select(x => new { x.UserTokenId, x.UserId, x.ConsumedAt, x.ExpiresAt })
            .SingleOrDefaultAsync(ct);
        if (row is null || row.ConsumedAt is not null) return new(TokenLookupStatus.Invalid, 0, 0);
        if (row.ExpiresAt <= time.GetUtcNow().UtcDateTime)
            return new(TokenLookupStatus.Expired, row.UserTokenId, row.UserId);
        return new(TokenLookupStatus.Valid, row.UserTokenId, row.UserId);
    }

    /// <summary>SQL UPDATE có điều kiện: chỉ đúng 1 request tiêu thụ được token.</summary>
    public async Task<bool> ConsumeAsync(string? token, string purpose, CancellationToken ct)
    {
        var hash = HashToken(token);
        if (hash is null) return false;
        var now = time.GetUtcNow().UtcDateTime;
        var changed = await db.UserTokens
            .Where(x => x.TokenHash == hash && x.Purpose == purpose &&
                x.ConsumedAt == null && x.ExpiresAt > now)
            .ExecuteUpdateAsync(up => up.SetProperty(x => x.ConsumedAt, now), ct);
        return changed == 1;
    }
}
