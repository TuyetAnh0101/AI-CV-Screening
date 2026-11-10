using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Prompts;

namespace SmartCv.Api.Endpoints;

/// <summary>A15: queue a prompt test and read persisted progress; scoring uses the existing A11 worker.</summary>
public static class PromptTestEndpoints
{
    public static WebApplication MapPromptTestEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() ||
            !app.Services.GetRequiredService<IOptions<CvUploadEndpointOptions>>().Value.Enabled) return app;
        var group = app.MapGroup("/api/prompt-tests").WithTags("PromptTests");
        group.MapPost("/", StartAsync).WithName("StartPromptTest")
            .RequireRateLimiting(CvUploadRateLimiting.PolicyName);
        group.MapGet("/", ListAsync).WithName("ListPromptTests");
        group.MapGet("/{runId:int}", GetAsync).WithName("GetPromptTest");
        return app;
    }

    private static async Task<IResult> StartAsync(StartPromptTestRequest request, SmartCvDbContext db,
        IOptions<ScoringOptions> options, CancellationToken ct)
    {
        var opt = options.Value;
        // Keep the A12 persisted settings validation used by Screening, including decimal precision.
        if (!opt.HasValidWeights() || !opt.HasValidScoreLimits() || string.IsNullOrWhiteSpace(opt.ModelName)
            || opt.ModelName.Length > 100 || opt.Temperature is < 0 or > 9.99m
            || new[] { opt.Temperature, opt.KeywordWeight, opt.AiWeight, opt.PassThreshold }
                .Any(v => decimal.Round(v, 2) != v))
            return Problem(500, "Cấu hình chấm điểm không hợp lệ", "Không thể lưu chính xác cấu hình chấm điểm của lượt chạy.");

        try
        {
            await using var transaction = await EvaluationSetWriteScope.BeginAsync(db, request.EvaluationSetId, ct);
            if (!await db.PromptVersions.AnyAsync(p => p.PromptVersionId == request.PromptVersionId, ct))
                return Problem(404, "Không tìm thấy prompt", "Phiên bản prompt không tồn tại.");
            if (!await db.EvaluationSets.AnyAsync(s => s.EvaluationSetId == request.EvaluationSetId, ct))
                return Problem(404, "Không tìm thấy bộ CV", "Bộ CV đánh giá không tồn tại.");

            var labels = await db.HrLabels.AsNoTracking().Where(h => h.EvaluationSetId == request.EvaluationSetId)
                .OrderBy(h => h.ApplicationId).Select(h => new { h.ApplicationId, h.IsPass }).ToListAsync(ct);
            if (labels.Count == 0)
                return Problem(400, "Bộ CV chưa có nhãn", "Hãy gán nhãn HR trước khi chạy đánh giá.");
            if (labels.All(h => h.IsPass) || labels.All(h => !h.IsPass))
                return Problem(400, "Nhãn chưa đủ hai loại", "Bộ đánh giá phải có cả hồ sơ Đạt và Không đạt.");

            var activeRunId = await db.EvaluationRuns.AsNoTracking()
                .Where(r => r.RunType == RunTypes.PromptTest && r.PromptVersionId == request.PromptVersionId
                    && r.EvaluationSetId == request.EvaluationSetId
                    && (r.Status == RunStatuses.Queued || r.Status == RunStatuses.Running))
                .OrderByDescending(r => r.RunId).Select(r => (int?)r.RunId).FirstOrDefaultAsync(ct);
            if (activeRunId is not null)
                return Results.Problem(statusCode: 409, title: "Đang chạy",
                    detail: $"Phiên bản này đang chạy trên bộ CV đã chọn (lượt #{activeRunId}).",
                    extensions: new Dictionary<string, object?> { ["runId"] = activeRunId });

            var now = DateTime.UtcNow;
            var run = new EvaluationRun
            {
                RunType = RunTypes.PromptTest, PromptVersionId = request.PromptVersionId,
                EvaluationSetId = request.EvaluationSetId, ModelName = opt.ModelName, Temperature = opt.Temperature,
                KeywordWeight = opt.KeywordWeight, AiWeight = opt.AiWeight, PassThreshold = opt.PassThreshold,
                Status = RunStatuses.Queued, TotalItems = labels.Count, CreatedAt = now,
                Evaluations = labels.Select(h => new Evaluation
                {
                    ApplicationId = h.ApplicationId, Status = EvaluationStatuses.Pending, CreatedAt = now
                }).ToList()
            };
            db.EvaluationRuns.Add(run);
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            var dto = await db.QueryRuns().SingleAsync(r => r.RunId == run.RunId, ct);
            return Results.Created($"/api/prompt-tests/{run.RunId}", dto);
        }
        catch (Exception ex) when (ex.GetBaseException() is SqlException { Number: 1205 or 2601 or 2627 })
        {
            return Problem(409, "Xung đột tạo lượt chạy", "Dữ liệu vừa được một yêu cầu khác thay đổi. Hãy tải lại và thử lại.");
        }
    }

    private static async Task<IResult> ListAsync(SmartCvDbContext db, CancellationToken ct, int? evaluationSetId = null) =>
        Results.Ok(await db.QueryRuns(evaluationSetId).ToListAsync(ct));

    private static async Task<IResult> GetAsync(int runId, SmartCvDbContext db, CancellationToken ct)
    {
        var dto = await db.LoadProgressAsync(runId, ct);
        return dto is null ? Problem(404, "Không tìm thấy lượt chạy", "Không có lượt đánh giá prompt đã chọn.") : Results.Ok(dto);
    }

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}
