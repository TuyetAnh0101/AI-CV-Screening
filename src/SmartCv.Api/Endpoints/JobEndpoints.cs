using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Endpoints;

/// <summary>API job: GET /api/jobs, GET /api/jobs/{id}. Thêm GET /api/health để giao diện kiểm tra kết nối.</summary>
public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        // Giao diện gọi để biết API còn sống không (hiện huy hiệu "Đã kết nối API")
        app.MapGet("/api/health", () => TypedResults.Ok(new { status = "ok", time = DateTime.UtcNow }))
            .WithTags("Health")
            .WithName("Health");

        var group = app.MapGroup("/api/jobs").WithTags("Jobs");

        // GET /api/jobs            -> chỉ job đang tuyển
        // GET /api/jobs?activeOnly=false -> tất cả job
        group.MapGet("/", GetJobsAsync).WithName("GetJobs").WithSummary("Danh sách job kèm kỹ năng và từ khóa");
        group.MapGet("/{id:int}", GetJobByIdAsync).WithName("GetJobById").WithSummary("Chi tiết một job");

        return app;
    }

    private static async Task<Ok<List<JobDto>>> GetJobsAsync(
        SmartCvDbContext db, CancellationToken ct, bool activeOnly = true)
    {
        var jobs = await ProjectJobs(db.Jobs.AsNoTracking().Where(j => !activeOnly || j.IsActive))
            .ToListAsync(ct);
        return TypedResults.Ok(jobs);
    }

    private static async Task<Results<Ok<JobDto>, NotFound>> GetJobByIdAsync(
        int id, SmartCvDbContext db, CancellationToken ct)
    {
        var job = await ProjectJobs(db.Jobs.AsNoTracking().Where(j => j.JobId == id))
            .FirstOrDefaultAsync(ct);
        return job is null ? TypedResults.NotFound() : TypedResults.Ok(job);
    }

    // Chuyển entity sang DTO ngay trong câu SQL (chỉ lấy cột cần dùng, không tải cả entity)
    private static IQueryable<JobDto> ProjectJobs(IQueryable<Job> query) =>
        query
            .OrderBy(j => j.JobId)
            .Select(j => new JobDto
            {
                JobId = j.JobId,
                Title = j.Title,
                Description = j.Description,
                MinGpa = j.MinGpa,
                MinYearsExperience = j.MinYearsExperience,
                IsActive = j.IsActive,
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
}
