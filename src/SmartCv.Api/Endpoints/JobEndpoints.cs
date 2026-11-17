using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartCv.Core.Common;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Jobs;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Endpoints;

/// <summary>A21: public Job reads retain their contract; HR writes/statistics use the existing Development gate.</summary>
public static class JobEndpoints
{
    private const long MaxBodyBytes = 256 * 1024;
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.Strict };
    private static readonly string[] RequestFields = ["title", "description", "minGpa", "minYearsExperience", "headcount", "closingDate"];

    public static WebApplication MapJobEndpoints(this WebApplication app)
    {
        // Giao diện gọi để biết API còn sống không (hiện huy hiệu "Đã kết nối API")
        app.MapGet("/api/health", () => TypedResults.Ok(new { status = "ok", time = DateTime.UtcNow }))
            .WithTags("Health")
            .WithName("Health");

        var internalAccess = app.Environment.IsDevelopment()
            && app.Services.GetRequiredService<IOptions<CvUploadEndpointOptions>>().Value.Enabled;
        var group = app.MapGroup("/api/jobs").WithTags("Jobs").AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        // GET /api/jobs            -> chỉ job đang tuyển
        // GET /api/jobs?activeOnly=false -> tất cả job
        group.MapGet("/", (SmartCvDbContext db, TimeProvider clock, CancellationToken ct, bool activeOnly = true) =>
            GetJobsAsync(db, clock, internalAccess, activeOnly, ct)).WithName("GetJobs").WithSummary("Danh sách Job và kỹ năng");
        group.MapGet("/{id:int}", (int id, SmartCvDbContext db, TimeProvider clock, CancellationToken ct) =>
            GetJobByIdAsync(id, db, clock, internalAccess, ct)).WithName("GetJobById").WithSummary("Chi tiết một Job");

        if (internalAccess)
        {
            group.MapPost("/", CreateAsync).WithName("CreateJob").Produces<JobDto>(201).ProducesProblem(400).ProducesProblem(409);
            group.MapPut("/{id:int}", UpdateAsync).WithName("UpdateJob").Produces<JobDto>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
            group.MapPost("/{id:int}/deactivate", (int id, SmartCvDbContext db, TimeProvider clock, CancellationToken ct) =>
                SetActiveAsync(id, false, db, clock, ct)).WithName("DeactivateJob").Produces<JobDto>().ProducesProblem(404);
            group.MapPost("/{id:int}/activate", (int id, SmartCvDbContext db, TimeProvider clock, CancellationToken ct) =>
                SetActiveAsync(id, true, db, clock, ct)).WithName("ActivateJob").Produces<JobDto>().ProducesProblem(404);
        }

        return app;
    }

    private static async Task<Ok<List<JobDto>>> GetJobsAsync(
        SmartCvDbContext db, TimeProvider clock, bool internalAccess, bool activeOnly, CancellationToken ct)
    {
        var jobs = await ReadJobsAsync(db, db.Jobs.AsNoTracking().Where(j => !activeOnly || j.IsActive), internalAccess, Today(clock), ct);
        return TypedResults.Ok(jobs);
    }

    private static async Task<Results<Ok<JobDto>, NotFound>> GetJobByIdAsync(
        int id, SmartCvDbContext db, TimeProvider clock, bool internalAccess, CancellationToken ct)
    {
        var job = (await ReadJobsAsync(db, db.Jobs.AsNoTracking().Where(j => j.JobId == id), internalAccess, Today(clock), ct)).SingleOrDefault();
        return job is null ? TypedResults.NotFound() : TypedResults.Ok(job);
    }

    // Chuyển entity sang DTO ngay trong câu SQL (chỉ lấy cột cần dùng, không tải cả entity)
    private static IQueryable<JobDto> ProjectJobs(IQueryable<Job> query, bool internalAccess) =>
        query
            .OrderByDescending(j => j.IsActive).ThenBy(j => j.JobId)
            .Select(j => new JobDto
            {
                JobId = j.JobId,
                Title = j.Title,
                Description = j.Description,
                MinGpa = j.MinGpa,
                MinYearsExperience = j.MinYearsExperience,
                IsActive = j.IsActive,
                Headcount = j.Headcount,
                ClosingDate = j.ClosingDate,
                CreatedAt = j.CreatedAt,
                MustHaveCount = j.Skills.Count(s => s.SkillType == SkillTypes.MustHave),
                HasEvaluations = internalAccess ? j.Applications.Any(a => a.Evaluations.Any()) : (bool?)null,
                Skills = j.Skills
                    .OrderBy(s => s.SkillType == SkillTypes.MustHave ? 0 : 1)
                    .ThenBy(s => s.DisplayName)
                    .Select(s => new JobSkillDto
                    {
                        SkillCode = s.SkillCode,
                        DisplayName = s.DisplayName,
                        SkillType = s.SkillType,
                        Aliases = s.Aliases.OrderBy(a => a.Alias).Select(a => a.Alias).ToList()
                    })
                    .ToList()
            })
            .AsSplitQuery();

    private static async Task<List<JobDto>> ReadJobsAsync(SmartCvDbContext db, IQueryable<Job> query, bool internalAccess, DateOnly today, CancellationToken ct)
    {
        var jobs = await ProjectJobs(query, internalAccess).ToListAsync(ct);
        var counts = new Dictionary<(int JobId, string Stage), int>();
        if (internalAccess && jobs.Count > 0)
        {
            var ids = jobs.Select(j => j.JobId).ToArray();
            counts = await db.Applications.AsNoTracking().Where(a => ids.Contains(a.JobId))
                .GroupBy(a => new { a.JobId, a.Stage }).Select(g => new { g.Key.JobId, g.Key.Stage, Count = g.Count() })
                .ToDictionaryAsync(g => new ValueTuple<int, string>(g.JobId, g.Stage), g => g.Count, ct);
        }
        return jobs.Select(j => new JobDto
        {
            JobId = j.JobId, Title = j.Title, Description = j.Description, MinGpa = j.MinGpa,
            MinYearsExperience = j.MinYearsExperience, IsActive = j.IsActive, Skills = j.Skills,
            Headcount = j.Headcount, ClosingDate = j.ClosingDate, IsExpired = JobRules.IsExpired(j.ClosingDate, today),
            CreatedAt = DateTime.SpecifyKind(j.CreatedAt, DateTimeKind.Utc), MustHaveCount = j.MustHaveCount,
            HasEvaluations = j.HasEvaluations,
            ApplicationCount = internalAccess ? ApplicationStages.All.Sum(s => counts.GetValueOrDefault((j.JobId, s))) : null,
            StageCounts = internalAccess ? ApplicationStages.All.ToDictionary(s => s, s => counts.GetValueOrDefault((j.JobId, s))) : null
        }).ToList();
    }

    private static async Task<IResult> CreateAsync(HttpContext http, SmartCvDbContext db, TimeProvider clock, ILogger<Job> logger, CancellationToken ct)
    {
        var (request, problem) = await ReadRequestAsync(http, ct);
        if (problem is not null) return problem;
        var now = clock.GetUtcNow().UtcDateTime;
        var errors = JobRules.Validate(request!, VietnamTime.Today(now), true);
        if (errors.Count > 0) return Invalid(errors);
        var title = JobRules.NormalizeTitle(request!.Title);
        if (await db.Jobs.AsNoTracking().AnyAsync(j => j.Title == title, ct)) return DuplicateTitle();
        var job = new Job { IsActive = true, CreatedAt = now };
        Apply(job, request);
        db.Jobs.Add(job);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (TitleConflict(ex)) { return DuplicateTitle(); }
        logger.LogInformation("Đã tạo vị trí tuyển dụng {JobId}", job.JobId);
        return TypedResults.Created($"/api/jobs/{job.JobId}", await LoadAsync(db, job.JobId, clock, ct));
    }

    private static async Task<IResult> UpdateAsync(int id, HttpContext http, SmartCvDbContext db, TimeProvider clock, ILogger<Job> logger, CancellationToken ct)
    {
        if (id <= 0) return Invalid(new() { ["id"] = ["Mã Job phải là số nguyên dương."] });
        var (request, problem) = await ReadRequestAsync(http, ct);
        if (problem is not null) return problem;
        var errors = JobRules.Validate(request!, Today(clock), false);
        if (errors.Count > 0) return Invalid(errors);
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.JobId == id, ct);
        if (job is null) return Missing(id);
        var title = JobRules.NormalizeTitle(request!.Title);
        if (await db.Jobs.AsNoTracking().AnyAsync(j => j.JobId != id && j.Title == title, ct)) return DuplicateTitle();
        Apply(job, request);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (TitleConflict(ex)) { return DuplicateTitle(); }
        logger.LogInformation("Đã cập nhật vị trí tuyển dụng {JobId}", id);
        return TypedResults.Ok(await LoadAsync(db, id, clock, ct));
    }

    private static async Task<IResult> SetActiveAsync(int id, bool active, SmartCvDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (id <= 0) return Invalid(new() { ["id"] = ["Mã Job phải là số nguyên dương."] });
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.JobId == id, ct);
        if (job is null) return Missing(id);
        if (job.IsActive != active)
        {
            job.IsActive = active;
            await db.SaveChangesAsync(ct);
        }
        return TypedResults.Ok(await LoadAsync(db, id, clock, ct));
    }

    private static async Task<JobDto> LoadAsync(SmartCvDbContext db, int id, TimeProvider clock, CancellationToken ct) =>
        (await ReadJobsAsync(db, db.Jobs.AsNoTracking().Where(j => j.JobId == id), true, Today(clock), ct)).Single();

    private static void Apply(Job job, JobUpsertRequest request)
    {
        job.Title = JobRules.NormalizeTitle(request.Title);
        job.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        // Preserve existing decimal(3,2)/(4,1) storage precision consistently across providers.
        job.MinGpa = request.MinGpa is { } gpa ? Math.Round(gpa, 2, MidpointRounding.AwayFromZero) : null;
        job.MinYearsExperience = Math.Round(request.MinYearsExperience, 1, MidpointRounding.AwayFromZero);
        job.Headcount = request.Headcount;
        job.ClosingDate = request.ClosingDate;
    }

    private static async Task<(JobUpsertRequest? Body, ProblemHttpResult? Problem)> ReadRequestAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Request.ContentLength == 0 || http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == false)
            return (null, Invalid(new() { ["body"] = ["Cần gửi thông tin Job."] }));
        if (http.Request.ContentLength > MaxBodyBytes)
            return (null, TypedResults.Problem(statusCode: 413, title: "Nội dung yêu cầu quá lớn", detail: "Body tối đa 256 KiB."));
        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MaxBodyBytes;
        if (!http.Request.HasJsonContentType())
            return (null, TypedResults.Problem(statusCode: 415, title: "Nội dung yêu cầu phải là JSON", detail: "Gửi Content-Type: application/json và body UTF-8."));
        try
        {
            var body = await http.Request.ReadFromJsonAsync<JobUpsertRequest>(RequestJson, ct);
            return body is null ? (null, Invalid(new() { ["body"] = ["Cần gửi thông tin Job."] })) : (body, null);
        }
        catch (JsonException ex)
        {
            var field = RequestFields.FirstOrDefault(f => string.Equals(ex.Path, "$." + f, StringComparison.OrdinalIgnoreCase)) ?? "body";
            return (null, Invalid(new() { [field] = ["JSON không hợp lệ. Kiểm tra kiểu dữ liệu, giới hạn số và định dạng ngày yyyy-MM-dd."] }));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413)
        {
            return (null, TypedResults.Problem(statusCode: 413, title: "Nội dung yêu cầu quá lớn", detail: "Body tối đa 256 KiB."));
        }
    }

    private static DateOnly Today(TimeProvider clock) => VietnamTime.Today(clock.GetUtcNow().UtcDateTime);
    private static bool TitleConflict(DbUpdateException ex) => ex.InnerException is SqlException sql
        && sql.Errors.Cast<SqlError>().Any(e => e.Number is 2601 or 2627 && e.Message.Contains("UQ_Jobs_Title", StringComparison.Ordinal));
    private static ProblemHttpResult Missing(int id) => TypedResults.Problem(statusCode: 404, title: "Không tìm thấy Job", detail: $"Không có Job #{id}.");
    private static ProblemHttpResult DuplicateTitle() => TypedResults.Problem(statusCode: 409, title: "Trùng tên Job",
        detail: "Tên vị trí đã được sử dụng. Hãy chọn tên khác.", extensions: new Dictionary<string, object?> { ["errors"] = new Dictionary<string, string[]> { ["title"] = ["Tên vị trí đã được sử dụng."] } });
    private static ProblemHttpResult Invalid(Dictionary<string, string[]> errors) => TypedResults.Problem(statusCode: 400,
        title: "Dữ liệu chưa hợp lệ", detail: string.Join(" ", errors.Values.SelectMany(e => e)), extensions: new Dictionary<string, object?> { ["errors"] = errors });
}
