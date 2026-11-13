using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;
using SmartCv.Core.Privacy;
using SmartCv.Core.Storage;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Privacy;

public sealed class RetentionProcessor(SmartCvDbContext db, CvAnonymizer anonymizer, IOptions<RetentionOptions> options, ICvFileStorage storage)
{
    public async Task<RetentionRunResultDto> RunOnceAsync(DateTime nowUtc, bool dryRun, CancellationToken ct)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Thời điểm phải là UTC.", nameof(nowUtc));
        if (!dryRun && nowUtc > DateTime.UtcNow)
            throw new ArgumentException("Không dùng thời điểm tương lai khi xử lý thật.", nameof(nowUtc));
        var recovery = storage is ICvErasureStorage files ? files.PendingErasureIds(options.Value.BatchSize).ToList() : [];
        var recoveryErrors = new List<string>();
        var recovered = 0;
        if (!dryRun)
            foreach (var id in recovery)
            {
                try
                {
                    var attempt = await anonymizer.AnonymizeAsync(id, "Hệ thống", true, nowUtc, ct, recoveryOnly: true);
                    if (attempt.Outcome is AnonymizeOutcome.Recovered or AnonymizeOutcome.AlreadyDone)
                        recovered++;
                    else
                        recoveryErrors.Add($"CV {id}: cần kiểm tra bản phục hồi trước khi xử lý tiếp.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    db.ChangeTracker.Clear();
                    recoveryErrors.Add($"CV {id}: chưa phục hồi được.");
                }
            }

        var active = RetentionRules.ActiveStages.ToArray();
        var due = await db.Cvs.AsNoTracking().Where(c => c.AnonymizedAt == null && c.RetainUntil != null && c.RetainUntil <= nowUtc && !c.Applications.Any(a => active.Contains(a.Stage))).Where(c => !recovery.Contains(c.CvId)).OrderBy(c => c.RetainUntil).ThenBy(c => c.CvId).Select(c => c.CvId).Take(options.Value.BatchSize - recovery.Count).ToListAsync(ct);
        var done = new List<Guid>();
        var errors = recoveryErrors;
        var skipped = 0;
        if (!dryRun)
            foreach (var id in due)
            {
                AnonymizeAttempt attempt;
                try
                {
                    attempt = await anonymizer.AnonymizeAsync(id, "Hệ thống", true, nowUtc, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    db.ChangeTracker.Clear();
                    errors.Add($"CV {id}: không xử lý được; cần kiểm tra kết nối/phục hồi.");
                    continue;
                }

                if (attempt.Outcome == AnonymizeOutcome.Done)
                    done.Add(id);
                else if (attempt.Outcome == AnonymizeOutcome.Failed)
                    errors.Add($"CV {id}: cần kiểm tra xử lý/phục hồi.");
                else
                    skipped++;
            }

        return new()
        {
            RanAt = nowUtc,
            DryRun = dryRun,
            DueCount = due.Count,
            AnonymizedCount = done.Count,
            FailedCount = errors.Count,
            SkippedCount = skipped,
            RecoveryCount = recovery.Count,
            RecoveredCount = recovered,
            RecoveryCvIds = recovery,
            CvIds = dryRun ? due : done,
            Errors = errors
        };
    }
}
