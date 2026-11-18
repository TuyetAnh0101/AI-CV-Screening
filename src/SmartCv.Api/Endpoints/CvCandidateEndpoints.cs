using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Candidates;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Candidates;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Endpoints;

/// <summary>A24 additions to the existing CV API; route registration retains the existing gate.</summary>
public static partial class CvEndpoints
{
    private static async Task<Results<Ok<AttachCandidateResponse>, ProblemHttpResult>> AttachCandidateAsync(
        Guid id, HttpContext http, SmartCvDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var (request, bodyError) = await CandidateRequestReader.ReadAsync<AttachCandidateRequest>(http.Request, ct);
        if (bodyError is not null) return bodyError;
        if (request is null) return CandidateEndpoints.InvalidBody();
        if (request.CandidateId is { } requestedId && (requestedId < 1 || request.FullName is not null
            || request.Email is not null || request.PhoneNumber is not null || request.ConfirmNew))
            return CandidateEndpoints.Invalid(new() { ["candidateId"] = ["Chỉ gửi candidateId hợp lệ hoặc thông tin người mới."] });

        await using var write = await CandidateWriteScope.BeginAsync(db, ct);
        var cv = await db.Cvs.FirstOrDefaultAsync(c => c.CvId == id, ct);
        if (cv is null) return TypedResults.Problem(title: "Không tìm thấy CV", statusCode: 404);
        if (cv.AnonymizedAt is not null)
            return TypedResults.Problem(title: "CV đã được ẩn danh hóa",
                detail: "Không gắn ứng viên vào CV đã ẩn danh hóa.", statusCode: 409);
        var previousId = cv.CandidateId;
        Candidate target;
        var created = false;
        if (request.CandidateId is { } candidateId)
        {
            var existing = await db.Candidates.FirstOrDefaultAsync(c => c.CandidateId == candidateId, ct);
            if (existing is null) return CandidateEndpoints.NotFound(candidateId);
            target = existing;
        }
        else
        {
            var (input, errors) = CandidateRules.Validate(request.FullName, request.Email, request.PhoneNumber);
            if (input is null) return CandidateEndpoints.Invalid(errors);
            if (!request.ConfirmNew)
            {
                // Include the current candidate too: repeating a create request must still ask HR.
                var matches = await CandidateQueries.FindDuplicatesAsync(db, input.Email, input.PhoneNumber, null, ct);
                if (matches.Count > 0)
                    return TypedResults.Ok(new AttachCandidateResponse
                    {
                        Outcome = AttachOutcomes.NeedsDecision, CvId = id, Matches = matches
                    });
            }
            target = new Candidate
            {
                FullName = input.FullName, Email = input.Email, PhoneNumber = input.PhoneNumber,
                CreatedAt = clock.GetUtcNow().UtcDateTime
            };
            db.Candidates.Add(target);
            created = true;
        }
        cv.Candidate = target;
        var removed = false;
        if (previousId is { } oldId && (created || oldId != target.CandidateId)
            && !await db.Cvs.AnyAsync(v => v.CandidateId == oldId && v.CvId != id, ct))
        {
            var previous = await db.Candidates.FirstOrDefaultAsync(c => c.CandidateId == oldId, ct);
            if (previous is not null) { db.Candidates.Remove(previous); removed = true; }
        }
        // One SaveChanges and one transaction: assignment, new contact and orphan cleanup succeed together.
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return TypedResults.Ok(new AttachCandidateResponse
        {
            Outcome = AttachOutcomes.Attached, CvId = id, Candidate = CandidateEndpoints.ToDto(target),
            CandidateCreated = created, PreviousCandidateRemoved = removed
        });
    }

    private static async Task<Results<Ok<CandidateSuggestionDto>, ProblemHttpResult>> CandidateSuggestionAsync(
        Guid id, SmartCvDbContext db, CancellationToken ct)
    {
        var cv = await db.Cvs.AsNoTracking().Where(c => c.CvId == id)
            .Select(c => new { c.CandidateId, c.ExtractedText, c.AnonymizedAt }).FirstOrDefaultAsync(ct);
        if (cv is null) return TypedResults.Problem(title: "Không tìm thấy CV", statusCode: 404);
        if (cv.AnonymizedAt is not null) return TypedResults.Problem(title: "CV đã được ẩn danh hóa", statusCode: 410);
        var suggestion = CandidateInfoExtractor.Suggest(cv.ExtractedText);
        var matches = await CandidateQueries.FindDuplicatesAsync(db, suggestion.Email, suggestion.PhoneNumber, cv.CandidateId, ct);
        return TypedResults.Ok(new CandidateSuggestionDto
        {
            CvId = id, CurrentCandidateId = cv.CandidateId, FullName = suggestion.FullName,
            Email = suggestion.Email, PhoneNumber = suggestion.PhoneNumber, Matches = matches
        });
    }
}
