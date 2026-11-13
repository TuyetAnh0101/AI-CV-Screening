using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Privacy;
using SmartCv.Core.Storage;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Privacy;

public enum AnonymizeOutcome
{
    Done,
    AlreadyDone,
    NotFound,
    Blocked,
    Failed,
    Recovered
}

public sealed record AnonymizeAttempt(AnonymizeOutcome Outcome, AnonymizeResultDto? Result, string? BlockReason);
/// <summary>SQL locks cover eligibility and writes. Private file recovery covers SQL failure, not distributed atomicity.</summary>
public sealed class CvAnonymizer(SmartCvDbContext db, ICvFileStorage storage, ILogger<CvAnonymizer> logger)
{
    public const string RemovedPath = "(da-xoa)";
    public static string AnonymizedHash(Guid id) => ("ANON" + id.ToString("N")).PadRight(64, '0');
    public async Task<AnonymizeAttempt> AnonymizeAsync(Guid cvId, string actor, bool automatic, DateTime nowUtc, CancellationToken ct, bool recoveryOnly = false)
    {
        // Never silently substitute InMemory semantics for the required SQL locking/rollback guarantees.
        if (!db.Database.IsSqlServer() || storage is not ICvErasureStorage files)
            return new(AnonymizeOutcome.Failed, null, "Kho lưu trữ chưa hỗ trợ phục hồi ẩn danh hóa.");
        CvPrivacyScope? privacy;
        try
        {
            privacy = await CvPrivacyScope.BeginAsync(db, true, ct);
        }
        catch (CvPrivacyBusyException)
        {
            return new(AnonymizeOutcome.Blocked, null, "Đang có thao tác đọc hoặc xử lý dữ liệu CV. Hãy thử lại sau.");
        }

        await using var privacyLease = privacy;
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var committed = false;
        var prepared = false;
        try
        {
            var cv = await db.Cvs.FromSqlInterpolated($"SELECT * FROM Cvs WITH (UPDLOCK, HOLDLOCK) WHERE CvId={cvId}").AsNoTracking().SingleOrDefaultAsync(ct);
            if (cv is null)
                return new(AnonymizeOutcome.NotFound, null, null);
            if (cv.AnonymizedAt is { } done)
            {
                files.CompleteErasure(cvId);
                await tx.CommitAsync(ct);
                return new(AnonymizeOutcome.AlreadyDone, new() { CvId = cvId, AlreadyAnonymized = true, AnonymizedAt = Utc(done) }, null);
            }

            // A previous process may have stopped after deleting the file but before committing SQL.
            // Repair it even if a subsequent HR decision now makes this CV ineligible for erasure.
            files.RollbackErasure(cvId);
            if (recoveryOnly)
            {
                await tx.CommitAsync(ct);
                return new(AnonymizeOutcome.Recovered, null, null);
            }

            // HOLDLOCK protects existing stages AND the CvId index range against new job assignments.
            var apps = await db.Applications.FromSqlInterpolated($"SELECT * FROM Applications WITH (UPDLOCK, HOLDLOCK) WHERE CvId={cvId}").AsNoTracking().ToListAsync(ct);
            var stages = apps.Select(a => a.Stage).ToList();
            var reason = automatic ? RetentionRules.IsDueForAnonymization(cv.RetainUntil, cv.AnonymizedAt, stages, nowUtc) ? null : "CV không còn đủ điều kiện hết hạn lưu." : RetentionRules.ManualBlockReason(stages);
            if (reason is not null)
                return new(AnonymizeOutcome.Blocked, null, reason);
            var appIds = apps.Select(a => a.ApplicationId).ToArray();
            if (cv.ExtractionStatus == ExtractionStatuses.Pending || await db.Evaluations.AnyAsync(e => appIds.Contains(e.ApplicationId) && (e.Run.Status == "Queued" || e.Run.Status == "Running"), ct))
                return new(AnonymizeOutcome.Blocked, null, "CV đang chờ hoặc đang xử lý. Hãy đợi tiến trình hoàn tất rồi thử lại.");
            if (await db.Cvs.AnyAsync(c => c.CvId != cvId && c.StoredPath == cv.StoredPath, ct))
                return new(AnonymizeOutcome.Blocked, null, "File đang được hồ sơ khác tham chiếu; cần kiểm tra trước khi xử lý.");
            // ExecuteUpdate can replace an alternate-key value without dropping its uniqueness constraint.
            var candidateId = cv.CandidateId;
            var evaluations = db.Evaluations.Where(e => appIds.Contains(e.ApplicationId));
            var evaluationIds = evaluations.Select(e => e.EvaluationId);
            await db.EvaluationSkills.Where(s => evaluationIds.Contains(s.EvaluationId)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Evidence, (string?)null), ct);
            var cleared = await evaluations.ExecuteUpdateAsync(s => s.SetProperty(e => e.Reasoning, (string?)null).SetProperty(e => e.AiRawResponse, (string?)null).SetProperty(e => e.ErrorMessage, (string?)null), ct);
            var now = Utc(nowUtc);
            var hash = AnonymizedHash(cvId);
            await db.Cvs.Where(c => c.CvId == cvId).ExecuteUpdateAsync(s => s.SetProperty(c => c.CandidateId, (int?)null).SetProperty(c => c.ExtractedText, (string?)null).SetProperty(c => c.ExtractionError, (string?)null).SetProperty(c => c.OriginalFileName, RetentionRules.AnonymizedFileName).SetProperty(c => c.StoredPath, RemovedPath).SetProperty(c => c.FileHash, hash).SetProperty(c => c.AnonymizedAt, now), ct);
            var candidateDeleted = candidateId is { } cid && !await db.Cvs.AnyAsync(c => c.CandidateId == cid, ct) && await db.Candidates.Where(c => c.CandidateId == cid).ExecuteDeleteAsync(ct) > 0;
            // SQL statements above have succeeded but remain uncommitted. Prepare a durable file rollback copy.
            prepared = true;
            var deleted = files.PrepareErasure(cvId, cv.StoredPath);
            await tx.CommitAsync(ct);
            committed = true;
            files.CompleteErasure(cvId);
            db.ChangeTracker.Clear();
            logger.LogInformation("Ẩn danh hóa CV {CvId}: hoàn tất", cvId); // never log actor, names, paths or raw exception messages
            return new(AnonymizeOutcome.Done, new() { CvId = cvId, AnonymizedAt = now, FileDeleted = deleted, CandidateDeleted = candidateDeleted, EvaluationsCleared = cleared }, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (!committed)
            {
                try
                {
                    await tx.RollbackAsync(CancellationToken.None);
                }
                catch
                { /* Commit outcome may be unknown: retain recovery copy, do not restore blindly. */
                    logger.LogError("CV {CvId}: cần xác minh transaction và bản phục hồi", cvId);
                    return new(AnonymizeOutcome.Failed, null, "Cần quản trị viên kiểm tra trạng thái và bản phục hồi CV trước khi thử lại.");
                }

                if (prepared)
                {
                    try
                    {
                        files.RollbackErasure(cvId);
                    }
                    catch
                    {
                        logger.LogError("CV {CvId}: cần phục hồi file trước khi tiếp tục", cvId);
                    }
                }
            }

            db.ChangeTracker.Clear();
            logger.LogError("CV {CvId}: ẩn danh hóa chưa hoàn tất ({ErrorType})", cvId, ex.GetType().Name);
            return new(AnonymizeOutcome.Failed, null, "Ẩn danh hóa chưa hoàn tất. Quản trị viên cần kiểm tra trạng thái và bản phục hồi; không tự gửi lại hàng loạt.");
        }
        finally
        {
            // Cancellation before commit must also restore a prepared original file.
            if (prepared && !committed && ct.IsCancellationRequested)
            {
                try
                {
                    await tx.RollbackAsync(CancellationToken.None);
                    files.RollbackErasure(cvId);
                }
                catch
                {
                    logger.LogError("CV {CvId}: cần kiểm tra phục hồi sau khi hủy", cvId);
                }
            }
        }
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
