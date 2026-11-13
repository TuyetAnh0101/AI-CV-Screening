using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// A41: phần dùng chung của POST ai-preview (chấm thử) và POST /api/prompts/assembled-preview (xem nội dung gửi AI),
/// để hai API chọn prompt, kiểm tra hồ sơ và báo lỗi giống hệt nhau. Chỉ đọc database (AsNoTracking), không ghi gì.
/// </summary>
internal static class PromptToolSupport
{
    public const string DraftName = "Nháp (chưa lưu)";

    /// <summary>
    /// Giới hạn dung lượng body của hai API. Prompt nháp tối đa 20.000 ký tự; client .NET mặc định escape tiếng Việt thành \uXXXX
    /// (6 byte/ký tự) nên cần khoảng 120 KB. Vượt giới hạn: Kestrel trả 413.
    /// </summary>
    public const long MaxRequestBytes = 256 * 1024;

    /// <summary>Prompt đã chọn. VersionId = null với bản nháp.</summary>
    internal sealed record ResolvedPrompt(string Source, int? VersionId, string Name, string SystemPrompt)
    {
        public bool IsDraft => Source == PromptSourceKinds.Draft;
    }

    /// <summary>Hồ sơ đã sẵn sàng để chấm: CV đọc xong, có nội dung, chưa ẩn danh hóa.</summary>
    internal sealed record ReadyApplication(int ApplicationId, int JobId, Guid CvId, string ExtractedText, string? CandidateName);

    /// <summary>
    /// Đọc body JSON không bắt buộc. Không có body (Content-Length: 0, POST rỗng như A10) → (null, null), cùng cách nhận biết với
    /// minimal API. Có body: phải là JSON (415 nếu không) và đọc được (400 nếu sai cú pháp, sai kiểu dữ liệu hoặc không phải UTF-8),
    /// thay cho trang lỗi kèm stack trace của Development. Body vượt <see cref="MaxRequestBytes"/>: Kestrel trả 413.
    /// </summary>
    public static async Task<(T? Body, ProblemHttpResult? Problem)> ReadJsonBodyAsync<T>(HttpContext http, CancellationToken ct) where T : class
    {
        if (http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true) return (null, null);
        if (!http.Request.HasJsonContentType())
            return (null, Problem(StatusCodes.Status415UnsupportedMediaType, "Nội dung yêu cầu phải là JSON",
                "Gửi header Content-Type: application/json kèm body JSON (UTF-8), hoặc không gửi body."));
        try
        {
            return (await http.Request.ReadFromJsonAsync<T>(ct), null);
        }
        catch (JsonException)
        {
            // Không đưa thông báo gốc ra ngoài: có thể chứa một phần nội dung prompt
            return (null, Problem(400, "JSON không hợp lệ",
                "Không đọc được nội dung yêu cầu. Kiểm tra cú pháp JSON, kiểu dữ liệu (applicationId, promptVersionId là số nguyên) và mã hóa UTF-8."));
        }
    }

    /// <summary>Kiểm tra nguồn prompt trong body, TRƯỚC mọi truy vấn database và trước khi giữ lượt chấm thử.</summary>
    public static ProblemHttpResult? ValidateSource(string? systemPrompt, int? promptVersionId)
    {
        if (systemPrompt is not null && promptVersionId is not null)
            return Problem(400, "Chỉ chọn một nguồn prompt",
                "Gửi systemPrompt (bản nháp) HOẶC promptVersionId (phiên bản đã lưu), không gửi cả hai. Bỏ cả hai để dùng bản đang kích hoạt.");

        if (systemPrompt is not null)
        {
            var length = systemPrompt.Trim().Length;
            if (length == 0)
                return Problem(400, "Prompt nháp đang trống",
                    "Nội dung system prompt nháp không được để trống. Bỏ trường systemPrompt nếu muốn dùng bản đang kích hoạt.");
            if (length > PromptEndpoints.MaxPromptLength)
                return Problem(400, "Prompt quá dài",
                    $"Tối đa {PromptEndpoints.MaxPromptLength:N0} ký tự (bản nháp đang có {length:N0}).");
        }

        if (promptVersionId is <= 0)
            return Problem(400, "Mã phiên bản không hợp lệ", "promptVersionId phải là số nguyên dương.");
        return null;
    }

    /// <summary>Nháp → phiên bản chỉ định → bản đang kích hoạt (A10). Bản nháp đã qua <see cref="ValidateSource"/>.</summary>
    public static async Task<(ResolvedPrompt? Prompt, ProblemHttpResult? Problem)> ResolvePromptAsync(
        SmartCvDbContext db, string? systemPrompt, int? promptVersionId, CancellationToken ct)
    {
        if (systemPrompt is not null)
            return (new ResolvedPrompt(PromptSourceKinds.Draft, null, DraftName, systemPrompt.Trim()), null);

        if (promptVersionId is { } id)
        {
            var saved = await db.PromptVersions.AsNoTracking().Where(p => p.PromptVersionId == id)
                .Select(p => new { p.VersionName, p.SystemPrompt }).FirstOrDefaultAsync(ct);
            return saved is null
                ? (null, Problem(404, "Không tìm thấy prompt", $"Không có phiên bản prompt #{id}."))
                : (new ResolvedPrompt(PromptSourceKinds.Version, id, saved.VersionName, saved.SystemPrompt), null);
        }

        var active = await db.PromptVersions.AsNoTracking().Where(p => p.IsActive)
            .Select(p => new { p.PromptVersionId, p.VersionName, p.SystemPrompt }).FirstOrDefaultAsync(ct);
        return active is null
            ? (null, Problem(409, "Chưa có prompt đang dùng", "Bảng PromptVersions chưa có phiên bản nào IsActive = 1."))
            : (new ResolvedPrompt(PromptSourceKinds.Active, active.PromptVersionId, active.VersionName, active.SystemPrompt), null);
    }

    /// <summary>404 khi không có hồ sơ; 409 khi CV đã ẩn danh hóa, chưa đọc xong, đọc lỗi hoặc không có chữ (cùng nội dung lỗi với A9/A10).</summary>
    public static async Task<(ReadyApplication? Application, ProblemHttpResult? Problem)> LoadReadyApplicationAsync(
        SmartCvDbContext db, int applicationId, CancellationToken ct)
    {
        var row = await db.Applications.AsNoTracking()
            .Where(a => a.ApplicationId == applicationId)
            .Select(a => new
            {
                a.JobId, a.CvId, a.Cv.ExtractionStatus, a.Cv.ExtractedText, a.Cv.AnonymizedAt,
                CandidateName = a.Cv.Candidate != null ? a.Cv.Candidate.FullName : null   // A37: để che họ tên trước khi gửi AI
            })
            .FirstOrDefaultAsync(ct);
        if (row is null)
            return (null, Problem(404, "Không tìm thấy hồ sơ", $"Không có hồ sơ #{applicationId}."));
        if (row.AnonymizedAt is not null)
            return (null, Problem(409, "CV đã được ẩn danh hóa",
                "Nội dung CV đã được xóa theo chính sách lưu trữ nên không thể chấm thử hay xem nội dung gửi AI."));

        // Xét trạng thái trước: khi đọc lại (A7), CV về Pending nhưng vẫn còn chữ cũ, không được dùng chữ đó.
        if (row.ExtractionStatus != ExtractionStatuses.Completed || string.IsNullOrWhiteSpace(row.ExtractedText))
            return (null, ScoringPreviewEndpoints.NotReady(row.ExtractionStatus));

        return (new ReadyApplication(applicationId, row.JobId, row.CvId, row.ExtractedText, row.CandidateName), null);
    }

    public static ProblemHttpResult JobNotFound(int jobId) =>
        Problem(404, "Không tìm thấy vị trí tuyển dụng", $"Hồ sơ gắn với vị trí #{jobId} nhưng vị trí này không còn trong hệ thống.");

    /// <summary>
    /// Kết quả ĐÃ LƯU gần nhất (Completed) của đúng ApplicationId, lượt Screening hoặc PromptTest. Một truy vấn, chỉ đọc.
    /// "Gần nhất" theo Evaluations.CreatedAt rồi EvaluationId (luôn xác định). Không đọc nội dung CV.
    /// </summary>
    public static async Task<CurrentEvaluationDto?> LoadCurrentEvaluationAsync(SmartCvDbContext db, int applicationId, CancellationToken ct)
    {
        var row = await db.Evaluations.AsNoTracking()
            .Where(e => e.ApplicationId == applicationId && e.Status == EvaluationStatuses.Completed)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.EvaluationId)
            .Select(e => new
            {
                e.EvaluationId, e.RunId, e.Run.RunType, e.Run.PromptVersionId, PromptVersionName = e.Run.PromptVersion.VersionName,
                e.Run.ModelName, e.KeywordScore, e.AiScore, e.FinalScore, e.ScoreSource, e.FitLabel, e.PredictedPass,
                e.YearsExperience, e.Reasoning, e.CreatedAt, RunCompletedAt = e.Run.CompletedAt,
                Matched = e.Skills.Where(s => s.IsMatched).OrderBy(s => s.SkillCode).Select(s => s.SkillCode).ToList(),
                Missing = e.Skills.Where(s => !s.IsMatched).OrderBy(s => s.SkillCode).Select(s => s.SkillCode).ToList()
            })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        return new CurrentEvaluationDto
        {
            EvaluationId = row.EvaluationId, RunId = row.RunId, RunType = row.RunType,
            PromptVersionId = row.PromptVersionId, PromptVersionName = row.PromptVersionName, ModelName = row.ModelName,
            KeywordScore = row.KeywordScore, AiScore = row.AiScore, FinalScore = row.FinalScore, ScoreSource = row.ScoreSource,
            FitLabel = row.FitLabel, PredictedPass = row.PredictedPass, YearsExperience = row.YearsExperience, Reasoning = row.Reasoning,
            MatchedSkills = row.Matched, MissingSkills = row.Missing,
            CreatedAt = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc),
            RunCompletedAt = row.RunCompletedAt is { } completed ? DateTime.SpecifyKind(completed, DateTimeKind.Utc) : null
        };
    }

    private static ProblemHttpResult Problem(int status, string title, string detail) =>
        TypedResults.Problem(title: title, detail: detail, statusCode: status);
}
