using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Candidates;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Privacy;

namespace SmartCv.Infrastructure.Candidates;

public enum CandidateErasureOutcome { Done, Missing, AlreadyErased, Blocked, Unavailable }
public sealed record CandidateErasureAttempt(CandidateErasureOutcome Outcome, EraseCandidateResultDto? Result = null,
    long? RequestId = null, DateTime? HandledAt = null);

/// <summary>One SQL transaction for withdrawal, all CV tombstones, contact deletion and the audit row.</summary>
public sealed class CandidateErasureService(SmartCvDbContext db, CvAnonymizer anonymizer, TimeProvider clock)
{
    public async Task<CandidateErasureAttempt> EraseAsync(int id, EraseCandidateRequest request, CancellationToken ct)
    {
        if (CandidateErasePlan.Validate(request.Confirm, request.HandledBy, request.Note).Count > 0)
            throw new ArgumentException("Validate the erasure request before processing.");
        // No fake transaction guarantees on InMemory or unsupported storage.
        if (!db.Database.IsSqlServer() || anonymizer.CandidateErasureFiles is not { } files)
        {
            if (await db.Candidates.AnyAsync(c => c.CandidateId == id, ct))
                return new(CandidateErasureOutcome.Unavailable);
            var prior = await PreviousAsync(id, ct);
            return prior is null ? new(CandidateErasureOutcome.Missing)
                : new(CandidateErasureOutcome.AlreadyErased, RequestId: prior.RequestId, HandledAt: Utc(prior.CreatedAt));
        }

        // No shared middleware lease on this endpoint: acquire exclusive privacy BEFORE A24 write/SQL locks.
        await using var privacy = await CvPrivacyScope.BeginAsync(db, true, ct);
        await using var write = await CandidateWriteScope.BeginAsync(db, ct);
        if (!await db.Candidates.AnyAsync(c => c.CandidateId == id, ct))
        {
            var prior = await PreviousAsync(id, ct);
            if (prior is null) return new(CandidateErasureOutcome.Missing);
            try
            {
                foreach (var value in (prior.CvIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (Guid.TryParse(value, out var cvId)) files.CompleteErasure(cvId);
            }
            catch { throw new CandidateErasureRecoveryException(); }
            return new(CandidateErasureOutcome.AlreadyErased, RequestId: prior.RequestId, HandledAt: Utc(prior.CreatedAt));
        }
        var cvs = await db.Cvs.AsNoTracking().Where(c => c.CandidateId == id)
            .OrderBy(c => c.CvId).Select(c => new { c.CvId, c.StoredPath, c.AnonymizedAt, c.ExtractionStatus }).ToListAsync(ct);
        if (cvs.Any(c => c.AnonymizedAt == null && c.ExtractionStatus == ExtractionStatuses.Pending)
            || await db.Evaluations.AnyAsync(e => e.Application.Cv.CandidateId == id
                && (e.Run.Status == RunStatuses.Queued || e.Run.Status == RunStatuses.Running), ct)
            || await db.Cvs.AnyAsync(other => other.CandidateId != id && db.Cvs.Any(owned =>
                owned.CandidateId == id && owned.AnonymizedAt == null && owned.StoredPath == other.StoredPath), ct))
            return new(CandidateErasureOutcome.Blocked);

        var batch = new CvFileErasureBatch(files);
        var commitAttempted = false;
        var committed = false;
        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var applications = await db.Applications.Where(a => a.Cv.CandidateId == id).ToListAsync(ct);
            var withdrawals = CandidateErasePlan.Withdrawals(applications.Select(a => (a.ApplicationId, a.Stage)));
            var byId = applications.ToDictionary(a => a.ApplicationId);
            foreach (var step in withdrawals)
            {
                var app = byId[step.ApplicationId];
                db.ApplicationEvents.Add(new ApplicationEvent
                {
                    ApplicationId = app.ApplicationId, FromStage = step.FromStage, ToStage = ApplicationStages.Rejected,
                    RejectReason = RejectReasons.CandidateWithdrew, Note = CandidateErasePlan.EventNote,
                    Actor = request.HandledBy!.Trim(), CreatedAt = now
                });
                app.Stage = ApplicationStages.Rejected;
                app.RejectReason = RejectReasons.CandidateWithdrew;
                app.StageUpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);
            foreach (var cv in cvs)
            {
                ct.ThrowIfCancellationRequested();
                if (cv.AnonymizedAt is null) batch.Prepare(cv.CvId, cv.StoredPath);
                else files.CompleteErasure(cv.CvId);
            }
            await anonymizer.ClearCandidateDataAsync(id, cvs.Select(c => c.CvId).ToArray(), now, ct);
            var deleted = await db.Candidates.Where(c => c.CandidateId == id).ExecuteDeleteAsync(ct) == 1;
            if (!deleted) throw new DbUpdateConcurrencyException();
            var count = cvs.Count(c => c.AnonymizedAt is null);
            var already = cvs.Count - count;
            var log = new CandidateDataRequest
            {
                CandidateId = id, RequestType = DataRequestTypes.Erase, HandledBy = request.HandledBy!.Trim(),
                Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
                ResultSummary = CandidateErasePlan.Summary(count, already, withdrawals.Count, true),
                CvIds = string.Join(",", cvs.Select(c => c.CvId)), CreatedAt = now
            };
            db.CandidateDataRequests.Add(log);
            await db.SaveChangesAsync(ct);
            commitAttempted = true;
            await write.CommitAsync(ct);
            committed = true;
            batch.Complete();
            return new(CandidateErasureOutcome.Done, new EraseCandidateResultDto
            {
                RequestId = log.RequestId, CandidateId = id, CvsAnonymized = count, CvsAlreadyAnonymized = already,
                ApplicationsWithdrawn = withdrawals.Count, CandidateDeleted = true, HandledAt = now
            });
        }
        catch
        {
            if (!commitAttempted)
            {
                // Only restore originals after SQL rollback is known. On an ambiguous commit, retain journals.
                try { await db.Database.CurrentTransaction!.RollbackAsync(CancellationToken.None); batch.Rollback(); }
                catch { throw new CandidateErasureRecoveryException(); }
            }
            else if (!committed)
            {
                // A39 recovery will inspect committed tombstones before restoring/purging each durable journal.
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                throw new CandidateErasureRecoveryException();
            }
            throw;
        }
        finally { db.ChangeTracker.Clear(); }
    }

    private Task<CandidateDataRequest?> PreviousAsync(int id, CancellationToken ct) =>
        db.CandidateDataRequests.AsNoTracking().Where(r => r.CandidateId == id && r.RequestType == DataRequestTypes.Erase)
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.RequestId).FirstOrDefaultAsync(ct);
    private static DateTime Utc(DateTime date) => DateTime.SpecifyKind(date, DateTimeKind.Utc);
}
