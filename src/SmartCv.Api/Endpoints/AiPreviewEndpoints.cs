using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Ai;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Gemini;
using SmartCv.Infrastructure.Scoring;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// A10: chấm THỬ một hồ sơ bằng keyword + AI, KHÔNG lưu gì:
///   POST /api/applications/{id}/ai-preview
/// Mỗi lần gọi tốn quota Gemini (trừ khi Gemini:UseFake = true) nên có giới hạn tần suất,
/// và dùng chung chốt chặn với API hồ sơ (A8): chỉ có ở Development cho đến khi có xác thực (M6).
/// A41:
///   - body KHÔNG bắt buộc: { "systemPrompt": "..." } (nháp, không lưu) hoặc { "promptVersionId": 3 }; không body → bản đang kích hoạt.
///   - giới hạn lượt chấm thử dùng chung cả ứng dụng (AiPreview:LimitPerWindow / WindowMinutes / MaxConcurrent), 429 khi hết.
///     Chỉ tính lượt khi THẬT SỰ bắt đầu gọi AI: yêu cầu sai (400/404/409) không mất lượt.
///   - GET /api/applications/ai-preview/quota: số lượt còn lại (không tốn lượt, không đọc database).
///   - trả thêm kết quả ĐÃ LƯU gần nhất của hồ sơ để so cạnh nhau; không tạo/sửa PromptVersions, EvaluationRuns, Evaluations...
/// </summary>
public static class AiPreviewEndpoints
{
    /// <summary>Gợi ý thử lại khi đang có đủ lượt chạy cùng lúc (một lượt chấm thử thường xong trong vài giây).</summary>
    private const int BusyRetryAfterSeconds = 5;

    /// <summary>Giờ hiển thị trong thông báo (Việt Nam không đổi giờ theo mùa); cùng quy ước "UTC+7" với giao diện.</summary>
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

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
            .WithSummary("Chấm thử một hồ sơ bằng keyword + AI (không lưu); body không bắt buộc: prompt nháp hoặc phiên bản đã lưu")
            .RequireRateLimiting(AiPreviewRateLimitPolicy.PolicyName)
            .WithMetadata(new RequestSizeLimitAttribute(PromptToolSupport.MaxRequestBytes))   // A41: prompt nháp tối đa 20.000 ký tự
            // Body tự đọc (PromptToolSupport.ReadJsonBodyAsync) để lỗi 400/415 là ProblemDetails tiếng Việt; không dùng .Accepts
            // vì routing sẽ tự trả 415 rỗng trước khi vào handler.
            .WithDescription("Body JSON không bắt buộc (UTF-8): { \"systemPrompt\": \"...\" } (nháp, tối đa 20.000 ký tự) " +
                             "hoặc { \"promptVersionId\": 3 }. Không gửi body: dùng bản đang kích hoạt.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            // Không có cookie/phiên đăng nhập nên không có nguy cơ CSRF (giống các API hồ sơ).
            .DisableAntiforgery();

        // A41: cùng chốt chặn Development ở trên. Không tốn lượt, không gọi AI, không đọc database.
        app.MapGet("/api/applications/ai-preview/quota", Quota)
            .WithTags("Applications")
            .WithName("GetAiPreviewQuota")
            .WithSummary("Số lượt chấm thử còn lại (dùng chung cả ứng dụng)");
        return app;
    }

    private static Ok<PreviewQuotaDto> Quota(PreviewQuota quota, IOptions<GeminiOptions> gemini) =>
        TypedResults.Ok(ToQuotaDto(quota.Peek(), gemini.Value.UseFake));

    private static async Task<Results<Ok<AiPreviewDto>, ProblemHttpResult>> PreviewAsync(
        int id, HttpContext http, SmartCvDbContext db, IAiEvaluator ai, IOptions<ScoringOptions> options,
        IOptions<GeminiOptions> gemini, PreviewQuota quota, CancellationToken ct)
    {
        // A41: kiểm tra body trước mọi truy vấn; không body (A10) = bản đang kích hoạt
        var (request, bodyProblem) = await PromptToolSupport.ReadJsonBodyAsync<AiPreviewRequest>(http, ct);
        if (bodyProblem is not null) return bodyProblem;
        var invalid = PromptToolSupport.ValidateSource(request?.SystemPrompt, request?.PromptVersionId);
        if (invalid is not null) return invalid;

        var opt = options.Value;
        var (application, applicationProblem) = await PromptToolSupport.LoadReadyApplicationAsync(db, id, ct);
        if (application is null) return applicationProblem!;

        var (prompt, promptProblem) = await PromptToolSupport.ResolvePromptAsync(db, request?.SystemPrompt, request?.PromptVersionId, ct);
        if (prompt is null) return promptProblem!;

        var job = await db.LoadJobProfileAsync(application.JobId, ct);
        if (job is null) return PromptToolSupport.JobNotFound(application.JobId);

        var keyword = KeywordScoringService.Score(job, application.ExtractedText, opt, DateOnly.FromDateTime(DateTime.Today));
        if (!keyword.HasApplicableCriteria)   // kiểm tra trước khi gọi AI để không tốn quota
            return TypedResults.Problem(title: "Vị trí chưa có tiêu chí chấm",
                detail: "Job không có kỹ năng và CV không có dữ liệu GPA / kinh nghiệm phù hợp nên không tính được điểm.", statusCode: 409);

        // A41: đọc kết quả đã lưu TRƯỚC khi giữ lượt để thời gian giữ chỗ chạy ngắn nhất
        var current = await PromptToolSupport.LoadCurrentEvaluationAsync(db, id, ct);

        // A41: mọi kiểm tra ở trên xong mới giữ lượt → yêu cầu sai không làm mất lượt, hết lượt thì không gọi AI.
        AiEvaluationResult aiResult;
        bool tryCounted;
        using (var reservation = quota.TryReserve())
        {
            if (!reservation.IsGranted) return Rejected(reservation, gemini.Value.UseFake, http.Response);

            // Keyword (ở trên) dùng nội dung gốc; bản gửi Gemini được che thông tin cá nhân trong GeminiEvaluationService (A37)
            // bằng AiPromptAssembler, cùng hàm với POST /api/prompts/assembled-preview (A41).
            aiResult = await ai.EvaluateAsync(new AiEvaluationRequest(opt.ModelName, opt.Temperature, prompt.SystemPrompt, job,
                application.ExtractedText, application.CandidateName), ct);

            // Attempts = 0: chưa gửi gì tới AI (ví dụ thiếu API key) → trả lại ĐÚNG vé của request này.
            // Đã gửi (kể cả 503, hết thời gian chờ, Gemini hết hạn mức): giữ lượt vì Gemini có thể đã nhận và tính hạn mức.
            // Ngoại lệ (người dùng hủy request...): không biết chắc đã gửi hay chưa → giữ lượt; using vẫn trả chỗ chạy.
            if (aiResult.Attempts == 0) reservation.Refund();
            tryCounted = !reservation.Refunded;
        }

        var combined = ScoreCombiner.Combine(keyword, aiResult, opt.KeywordWeight, opt.AiWeight, opt.PassThreshold, opt.MissingMustHaveCap);
        if (await db.Cvs.AnyAsync(c => c.CvId == application.CvId && c.AnonymizedAt != null, ct))
            return TypedResults.Problem(statusCode: 409, title: "CV đã được ẩn danh hóa trong lúc chấm thử");

        var left = quota.Peek();
        return TypedResults.Ok(new AiPreviewDto(
            prompt.Name, aiResult.IsSimulated ? Infrastructure.Ai.FakeAiEvaluator.ModelLabel : opt.ModelName,
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
            MissingMustHaveCap = opt.MissingMustHaveCap,
            PromptSource = prompt.Source,
            PromptVersionId = prompt.VersionId,
            IsDraft = prompt.IsDraft,
            RemainingTries = left.Remaining,
            TryLimit = left.Limit,
            QuotaResetsAt = left.ResetsAt?.UtcDateTime,
            TryCounted = tryCounted,
            CurrentEvaluation = current
        });
    }

    /// <summary>
    /// 429 kèm Retry-After (giây) và các trường máy đọc được: code, remaining, limit, windowMinutes, resetsAt (UTC), retryAfterSeconds.
    /// Hai loại: PreviewQuotaExhausted (hết lượt trong cửa sổ) và PreviewBusy (đang có đủ lượt chạy cùng lúc). Không tốn lượt.
    /// </summary>
    private static ProblemHttpResult Rejected(PreviewQuotaReservation reservation, bool simulated, HttpResponse response)
    {
        var s = reservation.Snapshot;
        if (reservation.Status == PreviewQuotaStatus.Busy)
        {
            response.Headers.RetryAfter = BusyRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return TypedResults.Problem(
                title: "Đang có lượt chấm thử khác",
                detail: $"Đang chạy {s.InFlight}/{s.MaxConcurrent} lượt chấm thử cùng lúc. Hãy đợi lượt trước xong rồi thử lại sau khoảng {BusyRetryAfterSeconds} giây.",
                statusCode: StatusCodes.Status429TooManyRequests,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "PreviewBusy", ["remaining"] = s.Remaining, ["limit"] = s.Limit,
                    ["inFlight"] = s.InFlight, ["maxConcurrent"] = s.MaxConcurrent, ["retryAfterSeconds"] = BusyRetryAfterSeconds
                });
        }

        var resetsAt = s.ResetsAt ?? reservation.DecidedAt + s.Window;
        var retryAfter = (int)(reservation.RetryAfter ?? TimeSpan.FromSeconds(1)).TotalSeconds;
        response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        var detail = $"Đã dùng hết {s.Limit} lượt chấm thử trong {(int)s.Window.TotalMinutes} phút. " +
                     $"Thử lại sau {LocalTime(resetsAt, reservation.DecidedAt)} (giờ Việt Nam, UTC+7)." +
                     (simulated ? " Đang dùng AI giả lập nên không tốn hạn mức Gemini." : string.Empty);
        return TypedResults.Problem(
            title: "Hết lượt chấm thử",
            detail: detail,
            statusCode: StatusCodes.Status429TooManyRequests,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "PreviewQuotaExhausted", ["remaining"] = 0, ["limit"] = s.Limit,
                ["windowMinutes"] = (int)s.Window.TotalMinutes, ["resetsAt"] = resetsAt.UtcDateTime,
                ["retryAfterSeconds"] = retryAfter, ["aiSimulated"] = simulated
            });
    }

    /// <summary>HH:mm giờ Việt Nam, làm tròn LÊN phút (14:05:20 → 14:06) để người dùng không thử lại quá sớm; khác ngày thì kèm ngày.</summary>
    private static string LocalTime(DateTimeOffset utc, DateTimeOffset now)
    {
        var local = utc.ToOffset(VietnamOffset);
        var remainder = local.Ticks % TimeSpan.TicksPerMinute;
        if (remainder != 0) local = local.AddTicks(TimeSpan.TicksPerMinute - remainder);
        var today = now.ToOffset(VietnamOffset).Date;
        return local.Date == today
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("HH:mm 'ngày' dd/MM", CultureInfo.InvariantCulture);
    }

    private static PreviewQuotaDto ToQuotaDto(PreviewQuotaSnapshot s, bool simulated) => new()
    {
        Remaining = s.Remaining, Limit = s.Limit, WindowMinutes = (int)s.Window.TotalMinutes,
        ResetsAt = s.ResetsAt?.UtcDateTime, InFlight = s.InFlight, MaxConcurrent = s.MaxConcurrent, AiSimulated = simulated
    };
}
