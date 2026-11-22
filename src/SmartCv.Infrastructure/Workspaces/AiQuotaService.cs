using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartCv.Core.Entities;
using SmartCv.Core.Workspaces;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Workspaces;

public sealed record AiQuotaSnapshot(int? Limit, int Used, DateTimeOffset ResetsAt)
{
    public bool CanReserve(int requested) => requested > 0 && (Limit is null || (long)Used + requested <= Limit.Value);
}

public sealed record PreviewQuotaDecision(bool Allowed, long? ReservationId, AiQuotaSnapshot Snapshot);

/// <summary>
/// A54: sổ giữ chỗ AI theo ngày Việt Nam. Đếm Units - UnitsReleased cho cả lượt đang chờ và đã gọi AI,
/// vì chỉ đếm AiScore của lượt Completed sẽ cho phép hai yêu cầu đồng thời vượt giới hạn.
/// </summary>
public sealed class AiQuotaService(SmartCvDbContext db, IWorkspaceContext context, TimeProvider clock)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public int CurrentWorkspaceId => context.WorkspaceId is > 0
        ? context.WorkspaceId.Value
        : throw new InvalidOperationException("Không có workspace đã xác minh.");

    public static (DateTime FromUtc, DateTime ToUtc, DateTimeOffset ResetsAt) VietnamDay(DateTimeOffset utc)
    {
        var local = utc.ToOffset(VietnamOffset);
        var startLocal = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, VietnamOffset);
        var endLocal = startLocal.AddDays(1);
        return (startLocal.UtcDateTime, endLocal.UtcDateTime, endLocal.ToUniversalTime());
    }

    public async Task<IDbContextTransaction?> BeginWriteTransactionAsync(CancellationToken ct)
    {
        if (!db.Database.IsSqlServer()) return null; // EF InMemory test fixture
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Đã có giao dịch; gọi CheckAsync trực tiếp trong giao dịch đó.");
        return await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    }

    /// <summary>
    /// Phải gọi trong transaction nếu ghi run. UPDLOCK trên đúng một workspace serialize các request nhận quota.
    /// </summary>
    public async Task<AiQuotaSnapshot> CheckAsync(int requested, CancellationToken ct)
    {
        if (requested <= 0) throw new ArgumentOutOfRangeException(nameof(requested));
        if (db.Database.IsSqlServer() && db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Kiểm tra để ghi quota bắt buộc có transaction SQL.");
        return await ReadAsync(forUpdate: true, ct);
    }

    public Task<AiQuotaSnapshot> SnapshotAsync(CancellationToken ct) => ReadAsync(forUpdate: false, ct);

    private async Task<AiQuotaSnapshot> ReadAsync(bool forUpdate, CancellationToken ct)
    {
        var workspaceId = CurrentWorkspaceId;
        var query = forUpdate && db.Database.IsSqlServer()
            ? db.Workspaces.FromSqlInterpolated($"SELECT * FROM dbo.Workspaces WITH (UPDLOCK, HOLDLOCK) WHERE WorkspaceId = {workspaceId}")
            : db.Workspaces.Where(w => w.WorkspaceId == workspaceId);
        var ws = await query.AsNoTracking().SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Không tìm thấy workspace.");
        if (!ws.IsActive) throw new InvalidOperationException("Workspace không còn hoạt động.");

        var (start, end, reset) = VietnamDay(clock.GetUtcNow());
        var used = await db.Set<AiUsageEntry>().AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId && e.CreatedAtUtc >= start && e.CreatedAtUtc < end)
            .SumAsync(e => (int?)(e.Units - e.UnitsReleased), ct) ?? 0;
        return new AiQuotaSnapshot(ws.Kind == WorkspaceKinds.Internal ? null : ws.DailyAiEvaluationLimit,
            used, reset);
    }

    /// <summary>Ghi trong CÙNG transaction với việc tạo EvaluationRun + Evaluations.</summary>
    public void AddRunReservation(int runId, int quantity, string source)
    {
        if (runId <= 0 || quantity <= 0 || source is not ("Screening" or "PromptTest"))
            throw new ArgumentException("Lượt chấm hoặc số lượng không hợp lệ.");
        db.Set<AiUsageEntry>().Add(new AiUsageEntry
        {
            WorkspaceId = CurrentWorkspaceId, RunId = runId, Source = source,
            Units = quantity, CreatedAtUtc = clock.GetUtcNow().UtcDateTime
        });
    }

    /// <summary>Đặt 1 vé preview, commit TRƯỚC khi gọi AI; không bao giờ giữ SQL lock trong lúc gọi Gemini.</summary>
    public async Task<PreviewQuotaDecision> ReservePreviewAsync(CancellationToken ct)
    {
        await using var tx = await BeginWriteTransactionAsync(ct);
        var s = await CheckAsync(1, ct);
        if (!s.CanReserve(1)) return new PreviewQuotaDecision(false, null, s);
        var entry = new AiUsageEntry
        {
            WorkspaceId = CurrentWorkspaceId, Source = "Preview", Units = 1,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime
        };
        db.Set<AiUsageEntry>().Add(entry);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return new PreviewQuotaDecision(true, entry.AiUsageEntryId, s);
    }

    /// <summary>AI trả Attempts=0: hoàn 1 vé. Không xóa audit và không xóa dữ liệu usage.</summary>
    public async Task RefundPreviewAsync(long reservationId, CancellationToken ct)
    {
        var entry = await db.Set<AiUsageEntry>().SingleOrDefaultAsync(e =>
            e.AiUsageEntryId == reservationId && e.Source == "Preview", ct);
        if (entry is null) throw new InvalidOperationException("Không tìm thấy vé preview trong workspace.");
        if (entry.UnitsReleased == 0)
        {
            entry.UnitsReleased = 1;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Chỉ gọi khi evaluation chắc chắn KHÔNG gọi AI; lưu cùng SaveChanges của Evaluation để không hoàn trùng sau restart.
    /// </summary>
    public async Task ReleaseUnusedRunUnitAsync(int runId, CancellationToken ct)
    {
        var entry = await db.Set<AiUsageEntry>().SingleOrDefaultAsync(e => e.RunId == runId, ct);
        if (entry is null) return; // run cũ trước migration A54
        if (entry.UnitsReleased >= entry.Units)
            throw new InvalidOperationException("Vượt quá số vé đã giữ của lượt chấm.");
        entry.UnitsReleased++;
    }

    /// <summary>Thông tin quota CV; phải gọi trong khóa ghi riêng cho workspace.</summary>
    public async Task<(int? Limit, int Used)> CvCountAsync(CancellationToken ct)
    {
        var id = CurrentWorkspaceId;
        var ws = await db.Workspaces.AsNoTracking().SingleAsync(w => w.WorkspaceId == id, ct);
        var used = await db.Cvs.AsNoTracking().CountAsync(c => c.AnonymizedAt == null, ct); // chỉ CV còn lưu, ẩn danh không tốn quota
        return (ws.Kind == WorkspaceKinds.Internal ? null : ws.MaxCvCount, used);
    }
}
