using Microsoft.EntityFrameworkCore;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Workspaces;

/// <summary>
/// A52 (nền tảng cho A54): worker chỉ biết ID bản ghi trong hàng đợi. Hàm ở đây đọc đúng workspace của MỘT bản ghi
/// (system scope hẹp, chỉ SELECT một cột) để worker mở <see cref="WorkspaceScope.Begin"/> rồi xử lý bản ghi trong workspace của nó.
/// </summary>
public static class WorkerWorkspaces
{
    public static async Task<int?> OfRunAsync(SmartCvDbContext db, int runId, CancellationToken ct)
    {
        using var system = WorkspaceScope.BeginSystem("worker: đọc workspace của lượt chấm");
        return await db.EvaluationRuns.AsNoTracking().Where(r => r.RunId == runId).Select(r => (int?)r.WorkspaceId).FirstOrDefaultAsync(ct);
    }

    public static async Task<int?> OfCvAsync(SmartCvDbContext db, Guid cvId, CancellationToken ct)
    {
        using var system = WorkspaceScope.BeginSystem("worker: đọc workspace của CV");
        return await db.Cvs.AsNoTracking().Where(c => c.CvId == cvId).Select(c => (int?)c.WorkspaceId).FirstOrDefaultAsync(ct);
    }
}
