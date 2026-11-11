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

/// <summary>
/// A15 queue/progress and A16 accuracy. Scoring uses the existing A11 worker.
/// A17 (read-only, never calls Gemini):
///   GET /api/prompt-tests/compare?baseRunId=&amp;candidateRunId=                         two specific Completed runs
///   GET /api/prompts/compare?baseVersionId=&amp;candidateVersionId=&amp;evaluationSetId=      latest Completed run of each version
/// </summary>
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
        group.MapGet("/compare", CompareRunsAsync).WithName("ComparePromptTests")
            .WithSummary("So sánh hai lượt đánh giá prompt đã hoàn thành");
        group.MapGet("/{runId:int}", GetAsync).WithName("GetPromptTest");
        app.MapGet("/api/prompts/compare", CompareVersionsAsync).WithTags("Prompts").WithName("ComparePromptVersions")
            .WithSummary("So sánh hai phiên bản prompt trên một bộ CV đánh giá");
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

            var labeledIds = labels.Select(h => h.ApplicationId).ToList();
            if (await db.Applications.AnyAsync(a => labeledIds.Contains(a.ApplicationId)
                && (a.Cv.ExtractionStatus != ExtractionStatuses.Completed || a.Cv.ExtractedText == null
                    || a.Cv.ExtractedText.Trim() == ""), ct))
                return Problem(400, "CV chưa sẵn sàng", "Có CV chưa trích xuất xong, lỗi hoặc không có nội dung chữ. Hãy kiểm tra hồ sơ trước khi chạy đánh giá.");

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
        Results.Ok(await db.LoadSummariesAsync(evaluationSetId, ct));

    private static async Task<IResult> GetAsync(int runId, SmartCvDbContext db, CancellationToken ct)
    {
        var dto = await db.LoadProgressAsync(runId, ct);
        return dto is null ? Problem(404, "Không tìm thấy lượt chạy", "Không có lượt đánh giá prompt đã chọn.") : Results.Ok(dto);
    }

    // ---------- A17: GET /api/prompt-tests/compare?baseRunId=&candidateRunId= ----------
    private static async Task<IResult> CompareRunsAsync(SmartCvDbContext db, CancellationToken ct,
        int? baseRunId = null, int? candidateRunId = null)
    {
        if (baseRunId is null || candidateRunId is null)
            return Problem(400, "Thiếu tham số", "Cần cả baseRunId và candidateRunId.");
        if (baseRunId <= 0 || candidateRunId <= 0)
            return Problem(400, "Tham số không hợp lệ", "baseRunId và candidateRunId phải là số nguyên dương.");
        if (baseRunId == candidateRunId)
            return Problem(400, "Cùng một lượt chạy", "Hãy chọn hai lượt chạy khác nhau để so sánh.");

        var runs = await db.LoadCompareRunsAsync([baseRunId.Value, candidateRunId.Value], ct);
        var baseRun = runs.SingleOrDefault(r => r.RunId == baseRunId);
        var candidateRun = runs.SingleOrDefault(r => r.RunId == candidateRunId);
        foreach (var (id, run) in new[] { (baseRunId.Value, baseRun), (candidateRunId.Value, candidateRun) })
        {
            if (run is null)
                return Problem(404, "Không tìm thấy lượt chạy", $"Không có lượt chạy #{id}.");
            if (run.RunType != RunTypes.PromptTest || run.EvaluationSetId is null)
                return Problem(400, "Không phải lượt đánh giá prompt",
                    $"Lượt #{id} là lượt {run.RunType}, chỉ so sánh được các lượt PromptTest chạy trên bộ CV có nhãn HR.");
            if (run.Status != RunStatuses.Completed)
                return Results.Problem(statusCode: 409, title: "Lượt chạy chưa hoàn thành",
                    detail: $"Lượt #{id} ({run.PromptVersionName}) đang ở trạng thái {run.Status}. Chỉ so sánh được lượt đã hoàn thành (Completed).",
                    extensions: new Dictionary<string, object?> { ["runId"] = id, ["runStatus"] = run.Status });
        }

        return Results.Ok(await db.BuildPromptCompareAsync(baseRun!, candidateRun!, ct));
    }

    // ---------- A17: GET /api/prompts/compare?baseVersionId=&candidateVersionId=&evaluationSetId= ----------
    private static async Task<IResult> CompareVersionsAsync(SmartCvDbContext db, CancellationToken ct,
        int? baseVersionId = null, int? candidateVersionId = null, int? evaluationSetId = null)
    {
        if (baseVersionId is null || candidateVersionId is null || evaluationSetId is null)
            return Problem(400, "Thiếu tham số", "Cần đủ baseVersionId, candidateVersionId và evaluationSetId.");
        if (baseVersionId <= 0 || candidateVersionId <= 0 || evaluationSetId <= 0)
            return Problem(400, "Tham số không hợp lệ", "baseVersionId, candidateVersionId và evaluationSetId phải là số nguyên dương.");
        if (baseVersionId == candidateVersionId)
            return Problem(400, "Cùng một phiên bản", "Hãy chọn hai phiên bản prompt khác nhau để so sánh.");

        var versions = await db.PromptVersions.AsNoTracking()
            .Where(p => p.PromptVersionId == baseVersionId || p.PromptVersionId == candidateVersionId)
            .Select(p => new { p.PromptVersionId, p.VersionName })
            .ToDictionaryAsync(p => p.PromptVersionId, p => p.VersionName, ct);
        foreach (var id in new[] { baseVersionId.Value, candidateVersionId.Value })
            if (!versions.ContainsKey(id))
                return Problem(404, "Không tìm thấy prompt", $"Không có phiên bản prompt #{id}.");
        if (!await db.EvaluationSets.AnyAsync(s => s.EvaluationSetId == evaluationSetId, ct))
            return Problem(404, "Không tìm thấy bộ CV", $"Không có bộ CV đánh giá #{evaluationSetId}.");

        var setId = evaluationSetId.Value;
        var baseRun = await db.LatestCompletedPromptTestAsync(baseVersionId.Value, setId, ct);
        var candidateRun = await db.LatestCompletedPromptTestAsync(candidateVersionId.Value, setId, ct);
        foreach (var (id, run) in new[] { (baseVersionId.Value, baseRun), (candidateVersionId.Value, candidateRun) })
        {
            if (run is not null) continue;
            var active = await db.LatestActivePromptTestAsync(id, setId, ct);
            var detail = $"Phiên bản {versions[id]} (#{id}) chưa có lượt đánh giá nào hoàn thành trên bộ CV #{setId}. " +
                         (active is null ? "Hãy chạy đánh giá trước." : $"Lượt #{active.RunId} đang {active.Status}; hãy đợi lượt này hoàn thành.");
            return Results.Problem(statusCode: 404, title: "Chưa có kết quả", detail: detail,
                extensions: new Dictionary<string, object?> { ["promptVersionId"] = id, ["activeRunId"] = active?.RunId });
        }

        return Results.Ok(await db.BuildPromptCompareAsync(baseRun!, candidateRun!, ct));
    }

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}
