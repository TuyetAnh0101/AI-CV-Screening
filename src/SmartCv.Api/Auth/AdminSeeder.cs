using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Auth;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Auth;

public static class AdminSeeder
{
    public static async Task SeedAdminAsync(this WebApplication app, CancellationToken ct = default)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await SeedAdminAsync(
            scope.ServiceProvider.GetRequiredService<SmartCvDbContext>(),
            scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>(),
            scope.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value.SeedAdmin,
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SmartCv.Auth.AdminSeeder"), ct);
    }

    public static async Task SeedAdminAsync(SmartCvDbContext db, IPasswordHasher<User> hasher,
        SeedAdminOptions? seed, ILogger logger, CancellationToken ct = default)
    {
        if (db.Database.IsSqlServer())
        {
            // Cùng resource cho mọi email và mọi tiến trình trong catalog. Recheck + insert nằm dưới cùng khóa.
            var createdUserId = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                await db.Database.ExecuteSqlRawAsync("""
                    DECLARE @result int;
                    EXEC @result = sys.sp_getapplock @Resource = N'SCApp.A27.FirstAdmin',
                        @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000;
                    IF @result < 0 THROW 51027, N'Không lấy được khóa tạo Admin đầu tiên. Hãy thử khởi động lại API.', 1;
                    """, ct);
                var userId = await SeedCoreAsync(db, hasher, seed, logger, ct);
                await transaction.CommitAsync(ct);
                return userId;
            });
            if (createdUserId is { } id) logger.LogInformation("Đã tạo tài khoản Admin đầu tiên #{UserId}.", id);
            return;
        }

        // Chỉ dùng cho unit tests InMemory; không giả lập bảo đảm khóa SQL Server trên provider khác.
        if (db.Database.IsRelational()) throw new NotSupportedException("AdminSeeder yêu cầu SQL Server.");
        if (await SeedCoreAsync(db, hasher, seed, logger, ct) is { } createdId)
            logger.LogInformation("Đã tạo tài khoản Admin đầu tiên #{UserId}.", createdId);
    }

    private static async Task<int?> SeedCoreAsync(SmartCvDbContext db, IPasswordHasher<User> hasher,
        SeedAdminOptions? seed, ILogger logger, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(u => u.Role == UserRoles.Admin && u.IsActive, ct)) return null;

        var decision = SeedAdminPlan.Decide(false, seed, _ => false);
        if (decision.Action == SeedAdminAction.Create && await db.Users.AnyAsync(u => u.Email == decision.Email, ct))
            decision = SeedAdminPlan.Decide(false, seed, _ => true);

        switch (decision.Action)
        {
            case SeedAdminAction.MissingConfig:
            case SeedAdminAction.EmailTaken:
                logger.LogWarning("{Message}", decision.Message);
                return null;
            case SeedAdminAction.Invalid:
                throw new InvalidOperationException(decision.Message);
        }

        var user = new User
        {
            Email = decision.Email!, FullName = decision.FullName!, Role = UserRoles.Admin,
            IsActive = true, MustChangePassword = false, CreatedAt = DateTime.UtcNow
        };
        user.PasswordHash = hasher.HashPassword(user, seed!.Password!);
        db.Users.Add(user);
        try { await db.SaveChangesAsync(ct); }
        catch
        {
            // Không để entity Added còn treo nếu execution strategy thử lại sau lỗi transient.
            db.Entry(user).State = EntityState.Detached;
            throw;
        }
        return user.UserId;
    }
}
