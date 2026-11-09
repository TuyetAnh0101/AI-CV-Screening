using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Scoring;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// A9: XEM TRƯỚC điểm keyword của một hồ sơ, KHÔNG lưu gì, KHÔNG gọi AI:
///   GET /api/applications/{id}/keyword-score
/// API trả tên file và trích đoạn CV (dữ liệu cá nhân) nên dùng chung chốt chặn với API hồ sơ (A8):
/// chỉ có ở Development khi CvUpload:Endpoint:Enabled = true, cho đến khi có xác thực (M6).
/// </summary>
public static class ScoringPreviewEndpoints
{
    public static WebApplication MapScoringPreviewEndpoints(this WebApplication app)
    {
        var endpointOptions = app.Services.GetRequiredService<IOptions<CvUploadEndpointOptions>>().Value;
        if (!endpointOptions.Enabled || !app.Environment.IsDevelopment())
        {
            app.Logger.LogInformation("API xem trước điểm keyword đang TẮT: chỉ bật ở Development khi chưa có xác thực (M6).");
            return app;
        }

        // M6: thêm .RequireAuthorization(...).
        app.MapGet("/api/applications/{id:int}/keyword-score", PreviewAsync)
            .WithTags("Applications")
            .WithName("PreviewKeywordScore")
            .WithSummary("Xem trước điểm keyword của một hồ sơ (không lưu, không gọi AI)")
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }

    private static async Task<Results<Ok<KeywordPreviewDto>, NotFound, ProblemHttpResult>> PreviewAsync(
        int id, SmartCvDbContext db, IOptions<ScoringOptions> options, CancellationToken ct)
    {
        var application = await db.Applications.AsNoTracking()
            .Where(a => a.ApplicationId == id)
            .Select(a => new { a.JobId, a.Cv.OriginalFileName, a.Cv.ExtractionStatus, a.Cv.ExtractedText })
            .FirstOrDefaultAsync(ct);
        if (application is null) return TypedResults.NotFound();

        // Xét trạng thái trước: khi đọc lại (A7), CV về Pending nhưng vẫn còn chữ cũ, không được chấm trên chữ đó.
        if (application.ExtractionStatus != ExtractionStatuses.Completed || string.IsNullOrWhiteSpace(application.ExtractedText))
            return NotReady(application.ExtractionStatus);

        var job = await db.LoadJobProfileAsync(application.JobId, ct);
        if (job is null) return TypedResults.NotFound();

        var result = KeywordScoringService.Score(job, application.ExtractedText, options.Value, DateOnly.FromDateTime(DateTime.Today));
        if (!result.HasApplicableCriteria)
            return TypedResults.Problem(title: "Vị trí chưa có tiêu chí chấm",
                detail: "Job không có kỹ năng và CV không có dữ liệu GPA / kinh nghiệm phù hợp nên không tính được điểm.", statusCode: 409);

        return TypedResults.Ok(new KeywordPreviewDto(
            id, application.OriginalFileName, job.Title, result.Score, result.Gpa, result.YearsExperience,
            result.Skills.Select(s => new SkillPreviewDto(
                s.SkillCode, s.DisplayName, s.IsMustHave ? SkillTypes.MustHave : SkillTypes.NiceToHave, s.IsMatched, s.Evidence)).ToList(),
            result.Notes.ToList()));
    }

    private static ProblemHttpResult NotReady(string extractionStatus)
    {
        var (title, detail) = extractionStatus switch
        {
            ExtractionStatuses.Pending => ("CV chưa có nội dung", "CV đang chờ đọc nội dung, hãy thử lại sau."),
            ExtractionStatuses.Failed => ("CV trích xuất lỗi", "Không đọc được nội dung CV nên không chấm được. Có thể đọc lại bằng POST /api/cvs/{id}/extract."),
            _ => ("CV chưa có nội dung", "Đã trích xuất nhưng CV không có chữ nên không chấm được.")
        };

        return TypedResults.Problem(title: title, detail: $"ExtractionStatus = {extractionStatus}. {detail}", statusCode: 409,
            extensions: new Dictionary<string, object?> { ["extractionStatus"] = extractionStatus });
    }
}
