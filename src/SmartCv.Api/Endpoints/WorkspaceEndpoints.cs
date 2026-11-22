using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using SmartCv.Api.Auditing;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Workspaces;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Workspaces;

namespace SmartCv.Api.Endpoints;

/// <summary>Hai API A54; chỉ map bên trong nhóm business RequireAuthorization của SmartCvApi.</summary>
public static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workspace").WithTags("Workspace").RequireAuthorization();
        group.MapGet("/", GetAsync).WithName("GetMyWorkspace");
        group.MapPost("/sample-job", SampleJobAsync).WithName("CreateWorkspaceSampleJob");
        return app;
    }

    private static async Task<IResult> GetAsync(
        SmartCvDbContext db, IWorkspaceContext current, AiQuotaService quota, CancellationToken ct)
    {
        if (current.WorkspaceId is not > 0 || current.IsSystemScope)
            return Results.Problem(statusCode: 403, title: "Không có không gian làm việc hợp lệ.");
        var id = current.WorkspaceId.Value;
        var ws = await db.Workspaces.AsNoTracking().SingleOrDefaultAsync(w => w.WorkspaceId == id && w.IsActive, ct);
        if (ws is null) return Results.NotFound();
        var snapshot = await quota.SnapshotAsync(ct);
        var dto = new WorkspaceDto(id, ws.Name, ws.Kind, current.Role ?? string.Empty,
            snapshot.Limit, snapshot.Used, snapshot.ResetsAt, ws.Kind == WorkspaceKinds.Internal ? null : ws.MaxCvCount,
            await db.Cvs.CountAsync(ct), await db.PromptVersions.AnyAsync(p => p.IsActive, ct),
            await db.Jobs.CountAsync(ct));
        return Results.Ok(dto);
    }

    private static async Task<IResult> SampleJobAsync(
        SmartCvDbContext db, IWorkspaceContext current, AuditTrail audit, TimeProvider clock, CancellationToken ct)
    {
        if (current.WorkspaceId is not > 0 || current.IsSystemScope)
            return Results.Problem(statusCode: 403, title: "Workspace không hợp lệ.");
        var id = current.WorkspaceId.Value;
        var ws = await db.Workspaces.AsNoTracking().SingleOrDefaultAsync(w => w.WorkspaceId == id && w.IsActive, ct);
        if (ws is null) return Results.NotFound();
        if (ws.Kind != WorkspaceKinds.Personal)
            return Results.Problem(statusCode: 403, title: "Chỉ workspace cá nhân mới được tạo Job mẫu.",
                extensions: new Dictionary<string, object?> { ["code"] = "not_personal_workspace" });
        if (await db.Jobs.AnyAsync(j => j.Title == SampleJobFactory.Title, ct))
            return Results.Problem(statusCode: 409, title: "Vị trí mẫu đã tồn tại.");

        await using var tx = db.Database.IsSqlServer()
            ? await db.Database.BeginTransactionAsync(ct) : null;
        var job = SampleJobFactory.Create(clock.GetUtcNow().UtcDateTime);
        db.Jobs.Add(job);
        try
        {
            await db.SaveChangesAsync(ct);
            audit.Stage(db, "workspace.sample_job", job.JobId.ToString(), workspaceId: id);
            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            audit.ConfirmCommitted();
        }
        catch (DbUpdateException ex) when (ex.GetBaseException() is SqlException { Number: 2601 or 2627 })
        {
            return Results.Problem(statusCode: 409, title: "Vị trí mẫu đã tồn tại.");
        }

        return Results.Created($"/api/jobs/{job.JobId}", new JobDto
        {
            JobId = job.JobId, Title = job.Title, Description = job.Description,
            MinGpa = job.MinGpa, MinYearsExperience = job.MinYearsExperience,
            IsActive = job.IsActive, CreatedAt = job.CreatedAt,
            MustHaveCount = job.Skills.Count(s => s.SkillType == "MustHave"),
            HasEvaluations = false, ApplicationCount = 0,
            Skills = job.Skills.Select(s => new JobSkillDto
            {
                SkillCode = s.SkillCode, DisplayName = s.DisplayName,
                SkillType = s.SkillType, Aliases = s.Aliases.Select(a => a.Alias).ToList()
            }).ToList()
        });
    }
}
