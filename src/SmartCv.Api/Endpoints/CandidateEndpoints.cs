using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Candidates;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Cvs;
using SmartCv.Core.Entities;
using SmartCv.Core.Privacy;
using SmartCv.Infrastructure.Candidates;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Endpoints;

/// <summary>A24 – API ứng viên. No automatic contact extraction, attachment or merge.</summary>
public static class CandidateEndpoints
{
    public static WebApplication MapCandidateEndpoints(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<IOptions<CvUploadEndpointOptions>>().Value.Enabled) return app;
        if (!app.Environment.IsDevelopment())
            throw new InvalidOperationException("Không được bật API ứng viên ngoài Development khi chưa có xác thực (M6).");

        var group = app.MapGroup("/api/candidates").WithTags("Candidates").AddEndpointFilter<CandidateApiFilter>();
        group.MapGet("/", ListAsync).WithName("ListCandidates").ProducesProblem(400);
        group.MapPost("/", CreateAsync).WithName("CreateCandidate").Accepts<CandidateRequest>("application/json").ProducesProblem(400).ProducesProblem(409);
        group.MapGet("/{id:int}", GetAsync).WithName("GetCandidate").ProducesProblem(404);
        group.MapPut("/{id:int}", UpdateAsync).WithName("UpdateCandidate").Accepts<CandidateRequest>("application/json").ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        group.MapPost("/{id:int}/merge", MergeAsync).WithName("MergeCandidate").Accepts<MergeCandidateRequest>("application/json").ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        return app;
    }

    private static async Task<Results<Ok<CandidatePageDto>, ProblemHttpResult>> ListAsync(
        HttpContext http, SmartCvDbContext db, CancellationToken ct)
    {
        // Reuse the existing pagination parser, including repeated/malformed values and overflow.
        var parameters = http.Request.Query.Where(p => p.Key.Equals("page", StringComparison.OrdinalIgnoreCase)
                || p.Key.Equals("pageSize", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Key, p => p.Value.Select(v => v ?? "").ToArray(), StringComparer.OrdinalIgnoreCase);
        var (query, errors) = CvListQuery.Parse(parameters);
        var searchValues = http.Request.Query["search"];
        var search = CandidateRules.NormalizeName(searchValues.FirstOrDefault());
        if (searchValues.Count > 1) errors["search"] = ["Mỗi tham số chỉ được gửi một lần."];
        if (search is { Length: > CandidateRules.MaxEmailLength })
            errors["search"] = [$"Từ khóa tối đa {CandidateRules.MaxEmailLength} ký tự."];
        if (query is null || errors.Count > 0) return Invalid(errors);

        var candidates = db.Candidates.AsNoTracking();
        if (search is not null)
        {
            var email = search.ToLowerInvariant();
            var phone = CandidateRules.NormalizePhone(search);
            candidates = candidates.Where(c => c.FullName.Contains(search)
                || (c.Email != null && c.Email.ToLower().Contains(email))
                || (phone != null && c.PhoneNumber == phone));
        }
        var total = await candidates.CountAsync(ct);
        var rows = await candidates.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.CandidateId)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(c => new
            {
                c.CandidateId, c.FullName, c.Email, c.PhoneNumber, c.CreatedAt,
                CvCount = c.Cvs.Count(),
                ApplicationCount = c.Cvs.SelectMany(v => v.Applications).Count(),
                LastUploadedAt = c.Cvs.Max(v => (DateTime?)v.UploadedAt)
            }).ToListAsync(ct);
        return TypedResults.Ok(new CandidatePageDto
        {
            Page = query.Page, PageSize = query.PageSize, TotalCount = total,
            TotalPages = CvListQuery.TotalPages(total, query.PageSize),
            Items = rows.Select(r => new CandidateListItemDto
            {
                CandidateId = r.CandidateId, FullName = r.FullName, Email = r.Email, PhoneNumber = r.PhoneNumber,
                CreatedAt = Utc(r.CreatedAt), CvCount = r.CvCount, ApplicationCount = r.ApplicationCount,
                LastUploadedAt = Utc(r.LastUploadedAt)
            }).ToList()
        });
    }

    private static async Task<Results<Created<CandidateDto>, ProblemHttpResult>> CreateAsync(
        HttpContext http, SmartCvDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var (request, bodyError) = await CandidateRequestReader.ReadAsync<CandidateRequest>(http.Request, ct);
        if (bodyError is not null) return bodyError;
        if (request is null) return InvalidBody();
        var (input, errors) = CandidateRules.Validate(request.FullName, request.Email, request.PhoneNumber);
        if (input is null) return Invalid(errors);
        await using var write = await CandidateWriteScope.BeginAsync(db, ct);
        if (!request.AllowDuplicate)
        {
            var matches = await CandidateQueries.FindDuplicatesAsync(db, input.Email, input.PhoneNumber, null, ct);
            if (matches.Count > 0) return PossibleDuplicate(matches);
        }
        var candidate = new Candidate
        {
            FullName = input.FullName, Email = input.Email, PhoneNumber = input.PhoneNumber,
            CreatedAt = clock.GetUtcNow().UtcDateTime
        };
        db.Candidates.Add(candidate);
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return TypedResults.Created($"/api/candidates/{candidate.CandidateId}", ToDto(candidate));
    }

    private static async Task<Results<Ok<CandidateDto>, ProblemHttpResult>> UpdateAsync(
        int id, HttpContext http, SmartCvDbContext db, CancellationToken ct)
    {
        var (request, bodyError) = await CandidateRequestReader.ReadAsync<CandidateRequest>(http.Request, ct);
        if (bodyError is not null) return bodyError;
        if (request is null) return InvalidBody();
        await using var write = await CandidateWriteScope.BeginAsync(db, ct);
        var candidate = await db.Candidates.FirstOrDefaultAsync(c => c.CandidateId == id, ct);
        if (candidate is null) return NotFound(id);
        var (input, errors) = CandidateRules.Validate(request.FullName, request.Email, request.PhoneNumber);
        if (input is null) return Invalid(errors);

        var changed = input.Email != CandidateRules.NormalizeEmail(candidate.Email)
            || input.PhoneNumber != CandidateRules.NormalizePhone(candidate.PhoneNumber);
        if (changed && !request.AllowDuplicate)
        {
            var matches = await CandidateQueries.FindDuplicatesAsync(db, input.Email, input.PhoneNumber, id, ct);
            if (matches.Count > 0) return PossibleDuplicate(matches);
        }
        candidate.FullName = input.FullName;
        candidate.Email = input.Email;
        candidate.PhoneNumber = input.PhoneNumber;
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return TypedResults.Ok(ToDto(candidate));
    }

    private static async Task<Results<Ok<CandidateDetailDto>, ProblemHttpResult>> GetAsync(
        int id, SmartCvDbContext db, CancellationToken ct)
    {
        var candidate = await db.Candidates.AsNoTracking().FirstOrDefaultAsync(c => c.CandidateId == id, ct);
        if (candidate is null) return NotFound(id);
        var cvs = await db.Cvs.AsNoTracking().Where(v => v.CandidateId == id)
            .OrderByDescending(v => v.UploadedAt).ThenBy(v => v.CvId)
            .Select(v => new
            {
                v.CvId, FileName = v.AnonymizedAt == null ? v.OriginalFileName : RetentionRules.AnonymizedFileName,
                v.UploadedAt, v.ExtractionStatus, v.SourceChannel, v.ConsentConfirmedAt, v.RetainUntil, v.AnonymizedAt
            }).ToListAsync(ct);
        var apps = await db.Applications.AsNoTracking().Where(a => a.Cv.CandidateId == id)
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.ApplicationId)
            .Select(a => new
            {
                a.ApplicationId, a.CvId, a.JobId, JobTitle = a.Job.Title, a.Stage, a.RejectReason, a.CreatedAt, a.StageUpdatedAt,
                Latest = a.Evaluations
                    .Where(e => e.Status == EvaluationStatuses.Completed && e.Run.RunType == RunTypes.Screening)
                    .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.EvaluationId)
                    .Select(e => new { e.FinalScore, e.FitLabel, e.CreatedAt }).FirstOrDefault()
            }).ToListAsync(ct);
        // Filter via the relationship rather than a huge IN list; one query for all timelines.
        var events = await db.ApplicationEvents.AsNoTracking().Where(e => e.Application.Cv.CandidateId == id)
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.EventId)
            .ToListAsync(ct);
        var byApp = events.GroupBy(e => e.ApplicationId).ToDictionary(g => g.Key, g => g.Select(e => new ApplicationEventDto
        {
            EventId = e.EventId, ApplicationId = e.ApplicationId, FromStage = e.FromStage, ToStage = e.ToStage,
            RejectReason = e.RejectReason, Note = e.Note, Actor = e.Actor, CreatedAt = Utc(e.CreatedAt)
        }).ToList());
        var duplicates = await CandidateQueries.FindDuplicatesAsync(db, candidate.Email, candidate.PhoneNumber, id, ct);
        return TypedResults.Ok(new CandidateDetailDto
        {
            CandidateId = candidate.CandidateId, FullName = candidate.FullName, Email = candidate.Email,
            PhoneNumber = candidate.PhoneNumber, CreatedAt = Utc(candidate.CreatedAt),
            Cvs = cvs.Select(v => new CandidateCvDto
            {
                CvId = v.CvId, FileName = v.FileName, UploadedAt = Utc(v.UploadedAt),
                ExtractionStatus = v.ExtractionStatus, SourceChannel = v.SourceChannel,
                SourceChannelName = CvSourceChannels.DisplayName(v.SourceChannel),
                ConsentConfirmedAt = Utc(v.ConsentConfirmedAt), RetainUntil = Utc(v.RetainUntil), AnonymizedAt = Utc(v.AnonymizedAt)
            }).ToList(),
            Applications = apps.Select(a => new CandidateApplicationDto
            {
                ApplicationId = a.ApplicationId, CvId = a.CvId, JobId = a.JobId, JobTitle = a.JobTitle,
                Stage = a.Stage, RejectReason = a.RejectReason, AppliedAt = Utc(a.CreatedAt), StageUpdatedAt = Utc(a.StageUpdatedAt),
                LatestFinalScore = a.Latest?.FinalScore, LatestFitLabel = a.Latest?.FitLabel,
                LatestEvaluatedAt = a.Latest is null ? null : Utc(a.Latest.CreatedAt),
                Events = byApp.GetValueOrDefault(a.ApplicationId) ?? []
            }).ToList(),
            PossibleDuplicates = duplicates
        });
    }

    private static async Task<Results<Ok<MergeCandidateResultDto>, ProblemHttpResult>> MergeAsync(
        int id, HttpContext http, SmartCvDbContext db, CancellationToken ct)
    {
        var (request, bodyError) = await CandidateRequestReader.ReadAsync<MergeCandidateRequest>(http.Request, ct);
        if (bodyError is not null) return bodyError;
        if (request is null) return InvalidBody();
        if (request.TargetCandidateId < 1 || request.TargetCandidateId == id)
            return Invalid(new() { ["targetCandidateId"] = ["Chọn một ứng viên khác hợp lệ để giữ lại."] });
        if (request.Actor is { Length: > 100 })
            return Invalid(new() { ["actor"] = ["Người thao tác tối đa 100 ký tự."] });
        await using var write = await CandidateWriteScope.BeginAsync(db, ct);
        var source = await db.Candidates.Include(c => c.Cvs).FirstOrDefaultAsync(c => c.CandidateId == id, ct);
        if (source is null) return NotFound(id);
        var target = await db.Candidates.FirstOrDefaultAsync(c => c.CandidateId == request.TargetCandidateId, ct);
        if (target is null) return NotFound(request.TargetCandidateId);
        var moved = source.Cvs.Count;
        foreach (var cv in source.Cvs.ToList()) cv.Candidate = target;
        // Keep the target's existing contact; normalize any missing contact inherited from legacy records.
        if (string.IsNullOrWhiteSpace(target.Email)) target.Email = CandidateRules.NormalizeEmail(source.Email);
        if (string.IsNullOrWhiteSpace(target.PhoneNumber)) target.PhoneNumber = CandidateRules.NormalizePhone(source.PhoneNumber);
        db.Candidates.Remove(source);
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        return TypedResults.Ok(new MergeCandidateResultDto
        {
            TargetCandidateId = target.CandidateId, RemovedCandidateId = id, CvsMoved = moved
        });
    }

    internal static CandidateDto ToDto(Candidate c) => new()
    {
        CandidateId = c.CandidateId, FullName = c.FullName, Email = c.Email,
        PhoneNumber = c.PhoneNumber, CreatedAt = Utc(c.CreatedAt)
    };

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    private static DateTime? Utc(DateTime? value) => value is { } date ? Utc(date) : null;
    internal static ProblemHttpResult Invalid(Dictionary<string, string[]> errors) => TypedResults.Problem(
        title: "Dữ liệu chưa hợp lệ", detail: string.Join(" ", errors.Values.SelectMany(e => e)), statusCode: 400,
        extensions: new Dictionary<string, object?> { ["errors"] = errors });
    internal static ProblemHttpResult InvalidBody() => Invalid(new() { ["body"] = ["Cần gửi nội dung JSON hợp lệ."] });
    internal static ProblemHttpResult NotFound(int id) =>
        TypedResults.Problem(title: "Không tìm thấy", detail: $"Không có ứng viên #{id}.", statusCode: 404);
    private static ProblemHttpResult PossibleDuplicate(List<DuplicateMatchDto> matches) => TypedResults.Problem(
        title: "Có thể trùng ứng viên",
        detail: "Đã có ứng viên cùng email hoặc số điện thoại. Hãy chọn người đã có hoặc xác nhận allowDuplicate nếu là người khác.",
        statusCode: 409, extensions: new Dictionary<string, object?> { ["matches"] = matches });
}
