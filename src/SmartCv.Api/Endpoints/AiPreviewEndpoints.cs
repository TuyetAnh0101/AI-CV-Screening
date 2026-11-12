using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Ai;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Scoring;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// A10: chấm THỬ một hồ sơ bằng keyword + AI với prompt ĐANG KÍCH HOẠT, KHÔNG lưu gì:
///   POST /api/applications/{id}/ai-preview
/// Mỗi lần gọi tốn quota Gemini (trừ khi Gemini:UseFake = true) nên có giới hạn tần suất,
/// và dùng chung chốt chặn với API hồ sơ (A8): chỉ có ở Development cho đến khi có xác thực (M6).
/// </summary>
public static class AiPreviewEndpoints
{
    public static WebApplication MapAiPreviewEndpoints(this WebApplication app)
    {
        var endpointOptions = app.Services.GetRequiredService<IOptions<CvUploadEndpointOptions>>().Value;
        if (!endpointOptions.Enabled || !app.Environment.IsDevelopment())
        {
            app.Logger.LogInformation("API chấm thử bằng AI đang TẮT: chỉ bật ở Development khi chưa có xác thực (M6).");
            return app;
        }

        // M6: thêm .RequireAuthorization(...).
        app.MapPost("/api/applications/{id:int}/ai-preview", PreviewAsync)
            .WithTags("Applications")
            .WithName("PreviewAiScore")
            .WithSummary("Chấm thử một hồ sơ bằng keyword + AI (không lưu)")
            .RequireRateLimiting(AiPreviewRateLimitPolicy.PolicyName)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            // Không có cookie/phiên đăng nhập nên không có nguy cơ CSRF (giống các API hồ sơ).
            .DisableAntiforgery();
        return app;
    }

    private static async Task<Results<Ok<AiPreviewDto>, NotFound, ProblemHttpResult>> PreviewAsync(
        int id, SmartCvDbContext db, IAiEvaluator ai, IOptions<ScoringOptions> options, CancellationToken ct)
    {
        var opt = options.Value;
        var application = await db.Applications.AsNoTracking()
            .Where(a => a.ApplicationId == id)
            .Select(a => new
            {
                a.JobId, a.Cv.ExtractionStatus, a.Cv.ExtractedText,
                CandidateName = a.Cv.Candidate != null ? a.Cv.Candidate.FullName : null   // A37: để che họ tên trước khi gửi AI
            })
            .FirstOrDefaultAsync(ct);
        if (application is null) return TypedResults.NotFound();

        if (application.ExtractionStatus != ExtractionStatuses.Completed || string.IsNullOrWhiteSpace(application.ExtractedText))
            return ScoringPreviewEndpoints.NotReady(application.ExtractionStatus);

        var prompt = await db.PromptVersions.AsNoTracking().Where(p => p.IsActive)
            .Select(p => new { p.VersionName, p.SystemPrompt }).FirstOrDefaultAsync(ct);
        if (prompt is null)
            return TypedResults.Problem(title: "Chưa có prompt đang dùng",
                detail: "Bảng PromptVersions chưa có phiên bản nào IsActive = 1.", statusCode: 409);

        var job = await db.LoadJobProfileAsync(application.JobId, ct);
        if (job is null) return TypedResults.NotFound();

        var keyword = KeywordScoringService.Score(job, application.ExtractedText, opt, DateOnly.FromDateTime(DateTime.Today));
        if (!keyword.HasApplicableCriteria)   // kiểm tra trước khi gọi AI để không tốn quota
            return TypedResults.Problem(title: "Vị trí chưa có tiêu chí chấm",
                detail: "Job không có kỹ năng và CV không có dữ liệu GPA / kinh nghiệm phù hợp nên không tính được điểm.", statusCode: 409);

        // Keyword (ở trên) dùng nội dung gốc; bản gửi Gemini được che thông tin cá nhân trong GeminiEvaluationService (A37).
        var aiResult = await ai.EvaluateAsync(new AiEvaluationRequest(opt.ModelName, opt.Temperature, prompt.SystemPrompt, job,
            application.ExtractedText, application.CandidateName), ct);
        var combined = ScoreCombiner.Combine(keyword, aiResult, opt.KeywordWeight, opt.AiWeight, opt.PassThreshold, opt.MissingMustHaveCap);

        return TypedResults.Ok(new AiPreviewDto(
            prompt.VersionName, aiResult.IsSimulated ? Infrastructure.Ai.FakeAiEvaluator.ModelLabel : opt.ModelName,
            combined.KeywordScore, combined.AiScore, combined.FinalScore,
            combined.ScoreSource, combined.FitLabel, combined.PredictedPass, combined.YearsExperience, combined.Reasoning,
            aiResult.IsSuccess, aiResult.ErrorKind.ToString(), combined.ErrorMessage, aiResult.Attempts, aiResult.IsSimulated, aiResult.CvTruncated,
            aiResult.MatchedSkillCodes.ToList(), aiResult.MissingSkillCodes.ToList(), aiResult.InvalidSkillCodes.ToList(),
            combined.Skills.Select(s => new AiPreviewSkillDto(
                s.SkillCode, s.DisplayName, s.IsMustHave ? SkillTypes.MustHave : SkillTypes.NiceToHave, s.IsMatched, s.MatchedBy, s.Evidence)).ToList())
        {
            KeywordWeight = opt.KeywordWeight,
            AiWeight = opt.AiWeight,
            PassThreshold = opt.PassThreshold,
            MissingMustHaveCap = opt.MissingMustHaveCap
        });
    }
}
