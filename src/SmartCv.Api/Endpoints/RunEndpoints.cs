using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.HttpResults;
using SmartCv.Core.Scoring;
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
        group.MapGet("/", ListAsync);
        group.MapGet("/latest", LatestAsync);
        group.MapGet("/{id:int}", GetAsync);
        group.MapGet("/{id:int}/ranking", RankingAsync);
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
    private static async Task<IResult> GetAsync(int id, SmartCvDbContext db, CancellationToken ct)
    {
        var run = await db.EvaluationRuns.AsNoTracking().Where(r => r.RunId == id)
            .Select(r => new RunProgressDto(r.RunId, r.RunType, r.Status, r.PromptVersionId,
                r.PromptVersion.VersionName, r.ModelName, r.Temperature, r.KeywordWeight, r.AiWeight,
                r.PassThreshold, r.TotalItems, r.ProcessedItems, r.FailedItems,
                0, r.CreatedAt, r.CompletedAt)).FirstOrDefaultAsync(ct);
        if (run is null) return Results.NotFound();
        var items = await db.Evaluations.AsNoTracking().Where(e => e.RunId == id)
            .OrderBy(e => e.EvaluationId).Select(e => new RunItemDto
            {
                EvaluationId = e.EvaluationId, ApplicationId = e.ApplicationId, CvId = e.Application.CvId,
                FileName = e.Application.Cv.OriginalFileName,
                CandidateName = e.Application.Cv.Candidate != null ? e.Application.Cv.Candidate.FullName : null,
                JobId = e.Application.JobId, JobTitle = e.Application.Job.Title, Status = e.Status,
                ExtractionStatus = e.Application.Cv.ExtractionStatus,
                KeywordScore = e.KeywordScore, AiScore = e.AiScore, FinalScore = e.FinalScore,
                ScoreSource = e.ScoreSource, FitLabel = e.FitLabel, PredictedPass = e.PredictedPass,
                ErrorMessage = e.ErrorMessage ?? (e.Application.Cv.ExtractionStatus == "Failed"
                    ? "CV trích xuất thất bại."
                    : e.Application.Cv.ExtractionStatus != "Completed" ? "Đang chờ trích xuất CV."
                    : e.ScoreSource == ScoreSources.KeywordOnly ? "AI lỗi, chỉ tính điểm từ khóa." : null)
            }).ToListAsync(ct);
        return Results.Ok(run with
        {
            Percent = RunScoringProcessor.ProgressPercent(run.ProcessedItems, run.TotalItems),
            CreatedAt = DateTime.SpecifyKind(run.CreatedAt, DateTimeKind.Utc),
            CompletedAt = run.CompletedAt is { } completed ? DateTime.SpecifyKind(completed, DateTimeKind.Utc) : null,
            Items = items
        });
    }

    // ---------- GET /api/runs ----------
    private static async Task<Ok<List<RunSummaryDto>>> ListAsync(
        SmartCvDbContext db, CancellationToken ct, string? type = null, int? jobId = null, int take = 20)
    {
        var query = db.EvaluationRuns.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(r => r.RunType == type);
        if (jobId is not null) query = query.Where(r => r.Evaluations.Any(e => e.Application.JobId == jobId));

        var list = await ProjectSummary(query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.RunId).Take(Math.Clamp(take, 1, 100)))
            .ToListAsync(ct);
        await FillJobsAsync(db, list, ct);
        return TypedResults.Ok(list);
    }

    // ---------- GET /api/runs/latest?jobId= ----------
    private static async Task<Results<Ok<RunSummaryDto>, NotFound>> LatestAsync(
        SmartCvDbContext db, CancellationToken ct, int? jobId = null)
    {
        var query = db.EvaluationRuns.AsNoTracking().Where(r => r.RunType == RunTypes.Screening);
        if (jobId is not null) query = query.Where(r => r.Evaluations.Any(e => e.Application.JobId == jobId));

        var run = await ProjectSummary(query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.RunId).Take(1)).FirstOrDefaultAsync(ct);
        if (run is null) return TypedResults.NotFound();
        await FillJobsAsync(db, [run], ct);
        return TypedResults.Ok(run);
    }

    private static IQueryable<RunSummaryDto> ProjectSummary(IQueryable<EvaluationRun> query) =>
        query.Select(r => new RunSummaryDto
        {
            RunId = r.RunId,
            RunType = r.RunType,
            Status = r.Status,
            PromptVersionName = r.PromptVersion.VersionName,
            ModelName = r.ModelName,
            TotalItems = r.TotalItems,
            ProcessedItems = r.ProcessedItems,
            FailedItems = r.FailedItems,
            AccuracyPercent = r.AccuracyPercent,
            CreatedAt = DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc),
            CompletedAt = r.CompletedAt == null ? null : DateTime.SpecifyKind(r.CompletedAt.Value, DateTimeKind.Utc)
        });

    /// <summary>Thêm danh sách job (kèm số hồ sơ) cho các lượt chấm: MỘT câu truy vấn GROUP BY cho tất cả.</summary>
    private static async Task FillJobsAsync(SmartCvDbContext db, List<RunSummaryDto> runs, CancellationToken ct)
    {
        var runIds = runs.Select(r => r.RunId).ToList();
        if (runIds.Count == 0) return;
        var rows = await db.Evaluations.AsNoTracking()
            .Where(e => runIds.Contains(e.RunId))
            .GroupBy(e => new { e.RunId, e.Application.JobId, e.Application.Job.Title })
            .Select(g => new { g.Key.RunId, g.Key.JobId, g.Key.Title, Count = g.Count() })
            .ToListAsync(ct);

        foreach (var run in runs)
        {
            run.Jobs.AddRange(rows.Where(x => x.RunId == run.RunId).OrderBy(x => x.JobId)
                .Select(x => new JobCountDto { JobId = x.JobId, Title = x.Title, Count = x.Count }));
        }
    }

    // ---------- GET /api/runs/{id}/ranking?jobId= ----------
    private static async Task<Results<Ok<RankingResponse>, NotFound<ProblemDetailsLite>>> RankingAsync(
        int id, SmartCvDbContext db, CancellationToken ct, int? jobId = null)
    {
        var run = await db.EvaluationRuns.AsNoTracking()
            .Where(r => r.RunId == id)
            .Select(r => new { r.RunId, r.Status, r.PassThreshold, PromptVersionName = r.PromptVersion.VersionName })
            .FirstOrDefaultAsync(ct);
        if (run is null) return TypedResults.NotFound(new ProblemDetailsLite("Không tìm thấy lượt chấm", 404));

        var jobs = await db.Evaluations.AsNoTracking()
            .Where(e => e.RunId == id)
            .GroupBy(e => new { e.Application.JobId, e.Application.Job.Title })
            .Select(g => new JobCountDto { JobId = g.Key.JobId, Title = g.Key.Title, Count = g.Count() })
            .OrderBy(j => j.JobId)
            .ToListAsync(ct);
        if (jobs.Count == 0) return TypedResults.NotFound(new ProblemDetailsLite("Lượt chấm không có hồ sơ", 404));

        // Không truyền jobId: lấy job đầu tiên trong lượt chấm
        var job = jobId is null ? jobs[0] : jobs.FirstOrDefault(j => j.JobId == jobId);
        if (job is null) return TypedResults.NotFound(new ProblemDetailsLite("Job này không có trong lượt chấm", 404));

        var rows = await db.Evaluations.AsNoTracking()
            .Where(e => e.RunId == id && e.Application.JobId == job.JobId)
            .Select(e => new
            {
                e.EvaluationId, e.ApplicationId, e.Application.CvId,
                e.Application.Stage, e.Application.RejectReason, e.Application.StageUpdatedAt, e.Application.StageVersion,
                FileName = e.Application.Cv.OriginalFileName,
                CandidateName = e.Application.Cv.Candidate != null ? e.Application.Cv.Candidate.FullName : null,
                e.KeywordScore, e.AiScore, e.FinalScore, e.ScoreSource, e.FitLabel, e.PredictedPass, e.YearsExperience,
                e.Status, e.ErrorMessage,
                MatchedMustHave = e.Skills.Count(s => s.IsMatched && s.JobSkill.SkillType == SkillTypes.MustHave),
                TotalMustHave = e.Application.Job.Skills.Count(s => s.SkillType == SkillTypes.MustHave)
            })
            .ToListAsync(ct);

        // Xếp hạng: điểm cao trước; bằng điểm thì cùng hạng (1, 2, 2, 4); chưa có điểm đứng cuối, không có hạng
        var scored = rows.Where(r => r.Status == EvaluationStatuses.Completed && r.FinalScore is not null).OrderByDescending(r => r.FinalScore).ThenBy(r => r.FileName).ThenBy(r => r.EvaluationId).ToList();
        var ranks = scored.Select((row, index) => new { Score = row.FinalScore!.Value, Rank = index + 1 })
            .GroupBy(x => x.Score).ToDictionary(g => g.Key, g => g.First().Rank);
        var items = scored.Select(r => (Row: r, Rank: (int?)ranks[r.FinalScore!.Value]))
            .Concat(rows.Where(r => r.Status != EvaluationStatuses.Completed || r.FinalScore is null).OrderBy(r => r.FileName).Select(r => (Row: r, Rank: (int?)null)))
            .Select(x => new RankingItemDto
            {
                Rank = x.Rank,
                EvaluationId = x.Row.EvaluationId,
                ApplicationId = x.Row.ApplicationId,
                Stage = x.Row.Stage,
                RejectReason = x.Row.RejectReason,
                StageUpdatedAt = x.Row.StageUpdatedAt is { } su ? DateTime.SpecifyKind(su, DateTimeKind.Utc) : null,
                StageVersion = x.Row.StageVersion,
                CvId = x.Row.CvId,
                FileName = x.Row.FileName,
                CandidateName = x.Row.CandidateName,
                KeywordScore = x.Row.KeywordScore,
                AiScore = x.Row.AiScore,
                FinalScore = x.Row.FinalScore,
                ScoreSource = x.Row.ScoreSource,
                FitLabel = x.Row.FitLabel,
                PredictedPass = x.Row.PredictedPass,
                YearsExperience = x.Row.YearsExperience,
                MatchedMustHave = x.Row.MatchedMustHave,
                TotalMustHave = x.Row.TotalMustHave,
                Status = x.Row.Status,
                ErrorMessage = x.Row.ErrorMessage
            })
            .ToList();

        var levelCounts = new[] { FitLevels.Excellent, FitLevels.Good, FitLevels.Potential, FitLevels.Poor }
            .ToDictionary(l => l, l => items.Count(i => i.Rank != null && i.FitLabel == l));

        return TypedResults.Ok(new RankingResponse
        {
            RunId = run.RunId,
            RunStatus = run.Status,
            PromptVersionName = run.PromptVersionName,
            PassThreshold = run.PassThreshold,
            JobId = job.JobId,
            JobTitle = job.Title,
            Jobs = jobs,
            LevelCounts = levelCounts,
            StageCounts = ApplicationStages.All.ToDictionary(s => s, s => items.Count(i => i.Stage == s)),
            PassCount = items.Count(i => i.Rank != null && i.PredictedPass == true),
            Items = items
        });
    }

}
public sealed record ProblemDetailsLite(string Title, int Status);
