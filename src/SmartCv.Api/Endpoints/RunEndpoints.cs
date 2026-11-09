using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Scoring;

namespace SmartCv.Api.Endpoints;

public static class RunEndpoints
{
    public static WebApplication MapRunEndpoints(this WebApplication app)
    {
        // Same access boundary as Applications until authentication is implemented.
        if (!app.Environment.IsDevelopment() ||
            !app.Services.GetRequiredService<IOptions<CvUploadEndpointOptions>>().Value.Enabled) return app;
        var group = app.MapGroup("/api/runs").WithTags("Runs");
        group.MapPost("/", StartAsync).RequireRateLimiting(CvUploadRateLimiting.PolicyName).DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> StartAsync(StartRunRequest request, SmartCvDbContext db,
        IOptions<ScoringOptions> options, CancellationToken ct)
    {
        var ids = (request.ApplicationIds ?? []).Distinct().ToArray();
        if (ids.Length is 0 or > 500 || ids.Any(id => id <= 0))
            return Results.Problem(statusCode: 400, title: "Cần từ 1 đến 500 mã hồ sơ hợp lệ.");
        if (await db.Applications.CountAsync(a => ids.Contains(a.ApplicationId), ct) != ids.Length)
            return Results.Problem(statusCode: 400, title: "Hồ sơ không tồn tại.");
        var prompts = await db.PromptVersions.AsNoTracking().Where(p => p.IsActive)
            .Select(p => new { p.PromptVersionId, p.VersionName }).Take(2).ToListAsync(ct);
        if (prompts.Count != 1)
            return Results.Problem(statusCode: 409, title: "Cần đúng một phiên bản prompt đang hoạt động.");
        var opt = options.Value;
        // Reject values that SQL decimal columns would round.
        if (!opt.HasValidWeights() || !opt.HasValidScoreLimits() || string.IsNullOrWhiteSpace(opt.ModelName)
            || opt.ModelName.Length > 100 || opt.Temperature is < 0 or > 9.99m
            || new[] { opt.Temperature, opt.KeywordWeight, opt.AiWeight, opt.PassThreshold }
                .Any(v => decimal.Round(v, 2) != v))
            return Results.Problem(statusCode: 500, title: "Cấu hình chấm điểm không thể lưu chính xác.");
        var now = DateTime.UtcNow;
        var run = new EvaluationRun
        {
            RunType = RunTypes.Screening, PromptVersionId = prompts[0].PromptVersionId,
            ModelName = opt.ModelName, Temperature = opt.Temperature,
            KeywordWeight = opt.KeywordWeight, AiWeight = opt.AiWeight, PassThreshold = opt.PassThreshold,
            Status = RunStatuses.Queued, TotalItems = ids.Length, CreatedAt = now,
            Evaluations = ids.Select(id => new Evaluation
            {
                ApplicationId = id, Status = EvaluationStatuses.Pending, CreatedAt = now
            }).ToList()
        };
        db.EvaluationRuns.Add(run);
        await db.SaveChangesAsync(ct); // Run and Pending evaluations share EF's transaction.
        // The read/progress endpoint for this resource belongs to A13.
        return Results.Created($"/api/runs/{run.RunId}", new RunProgressDto(
            run.RunId, run.RunType, run.Status, run.PromptVersionId, prompts[0].VersionName,
            run.ModelName, run.Temperature, run.KeywordWeight, run.AiWeight, run.PassThreshold,
            run.TotalItems, run.ProcessedItems, run.FailedItems,
            RunScoringProcessor.ProgressPercent(run.ProcessedItems, run.TotalItems), run.CreatedAt, run.CompletedAt));
    }
}
