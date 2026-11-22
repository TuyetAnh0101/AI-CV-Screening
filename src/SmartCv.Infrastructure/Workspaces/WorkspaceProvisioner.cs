using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Core.Workspaces;
using SmartCv.Infrastructure.Data;


namespace SmartCv.Infrastructure.Workspaces;

/// <summary>
/// Dùng bởi các luồng A56/A58 TRƯỚC khi phát JWT có workspace. Không map service này thành public API.
/// Transaction bảo đảm Workspace + Owner + Prompt + Audit cùng thành công hoặc cùng rollback.
/// </summary>
public sealed class WorkspaceProvisioner(SmartCvDbContext db, IOptions<WorkspaceOptions> options, TimeProvider clock)
{
    public async Task<Workspace> EnsurePersonalAsync(User user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.UserId <= 0) throw new ArgumentException("Người dùng chưa được lưu.", nameof(user));
        await using var tx = db.Database.IsSqlServer()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;

        // Khóa người dùng, không cho 2 luồng đăng ký tạo 2 workspace cùng lúc.
        var saved = db.Database.IsSqlServer()
            ? await db.Users.FromSqlInterpolated($"SELECT * FROM dbo.Users WITH (UPDLOCK, HOLDLOCK) WHERE UserId = {user.UserId}")
                .AsNoTracking().SingleOrDefaultAsync(ct)
            : await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == user.UserId, ct);
        if (saved is null || !saved.IsActive)
            throw new InvalidOperationException("Tài khoản không tồn tại hoặc đã bị khóa.");

        var existing = await db.Workspaces.SingleOrDefaultAsync(w =>
            w.Kind == WorkspaceKinds.Personal && w.OwnerUserId == saved.UserId, ct);
        if (existing is not null)
        {
            if (tx is not null) await tx.CommitAsync(ct);
            return existing;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var baseName = "Không gian của " + saved.FullName.Trim();
        var ws = new Workspace
        {
            Name = baseName.Length <= 100 ? baseName : baseName[..100],
            Kind = WorkspaceKinds.Personal,
            OwnerUserId = saved.UserId,
            IsActive = true,
            DailyAiEvaluationLimit = options.Value.PersonalDailyAiEvaluations,
            MaxCvCount = options.Value.PersonalMaxCvs,
            CreatedAt = now
        };
        db.Workspaces.Add(ws);
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Workspace = ws, UserId = saved.UserId,
            Role = WorkspaceRoles.Owner, JoinedAt = now
        });
        await db.SaveChangesAsync(ct); // sinh WorkspaceId trong transaction chưa commit

        using (WorkspaceScope.Begin(ws.WorkspaceId, WorkspaceRoles.Owner, WorkspaceKinds.Personal))
        {
            db.PromptVersions.Add(new PromptVersion
            {
                WorkspaceId = ws.WorkspaceId, VersionName = DefaultPromptTemplate.VersionName,
                SystemPrompt = DefaultPromptTemplate.Text,
                ChangeNote = "Prompt mặc định cho không gian cá nhân", IsActive = true,
                CreatedAt = now
            });
            db.AuditEvents.Add(new AuditEvent
            {
                WorkspaceId = ws.WorkspaceId, OccurredAtUtc = now,
                ActionCode = "workspace.provisioned", Category = "Workspace", ResourceType = "Workspace",
                ResourceId = ws.WorkspaceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Outcome = "Succeeded", RequestId = Guid.NewGuid().ToString("N"),
                SafeSummary = "Khởi tạo không gian cá nhân và prompt mặc định"
            });
            await db.SaveChangesAsync(ct);
        }

        if (tx is not null) await tx.CommitAsync(ct);
        return ws;
    }
}
