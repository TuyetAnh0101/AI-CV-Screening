using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Privacy;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Privacy;

/// <summary>Caller owns the decision transaction. Atomic SQL max prevents cross-job lost updates.</summary>
public static class CvRetentionWriter
{
    public static async Task ExtendAsync(SmartCvDbContext db, IEnumerable<Guid> cvIds, string toStage, DateTime nowUtc, int months, CancellationToken ct)
    {
        var deadline = RetentionRules.RetainUntilAfterStageChange(toStage, nowUtc, months, null);
        if (deadline is null) return;
        var ids = cvIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        var query = db.Cvs.Where(c => ids.Contains(c.CvId) && (c.RetainUntil == null || c.RetainUntil < deadline));
        if (db.Database.IsRelational())
        {
            if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Cập nhật hạn lưu phải cùng transaction với quyết định HR.");
            await query.ExecuteUpdateAsync(s => s.SetProperty(c => c.RetainUntil, deadline), ct);
        }
        else
        {
            // InMemory tests: tracked changes share the same single SaveChanges as applications/events.
            foreach (var cv in await query.ToListAsync(ct)) cv.RetainUntil = deadline;
        }
    }
}
