using System.Linq.Expressions;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Ai;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Gemini;
using SmartCv.Infrastructure.Scoring;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// A14: phiên bản system prompt.
///   GET  /api/prompts                 danh sách (mới nhất trước) kèm tỷ lệ đúng gần nhất
///   GET  /api/prompts/active          bản đang dùng
///   GET  /api/prompts/{id}            một bản
///   POST /api/prompts                 tạo bản mới (KHÔNG có API sửa: prompt đã tạo là cố định, sửa = lưu thành bản mới)
///   POST /api/prompts/{id}/activate   kích hoạt; bản đang dùng cũ tự tắt (chỉ một bản IsActive = 1)
/// A41:
///   POST /api/prompts/assembled-preview   nội dung SẼ gửi Gemini cho một hồ sơ (đã che dữ liệu cá nhân);
///                                         không gọi Gemini, không tốn lượt chấm thử, không ghi database
/// </summary>
public static class PromptEndpoints
{
    public const int MaxPromptLength = 20_000;   // ~5.000 token: đủ dài cho quy tắc chấm, không làm phình chi phí Gemini

    public static WebApplication MapPromptEndpoints(this WebApplication app)
    {
        // Same development-only boundary as the CV/results APIs until authentication exists.
        if (!app.Environment.IsDevelopment() ||
            !app.Services.GetRequiredService<IOptions<CvUploadEndpointOptions>>().Value.Enabled) return app;
        var group = app.MapGroup("/api/prompts").WithTags("Prompts");
        group.MapGet("/", ListAsync).WithName("ListPrompts").WithSummary("Danh sách phiên bản prompt");
        group.MapGet("/active", ActiveAsync).WithName("GetActivePrompt").WithSummary("Phiên bản đang dùng");
        group.MapGet("/{id:int}", GetAsync).WithName("GetPrompt").WithSummary("Một phiên bản prompt");
        group.MapPost("/", CreateAsync).WithName("CreatePrompt").WithSummary("Lưu thành phiên bản mới");
        group.MapPost("/{id:int}/activate", ActivateAsync).WithName("ActivatePrompt").WithSummary("Kích hoạt một phiên bản");
        // A41: trả nội dung CV (đã che) và thông tin job nên nằm trong cùng chốt chặn Development ở trên
        group.MapPost("/assembled-preview", AssembledPreviewAsync).WithName("PreviewAssembledPrompt")
            .WithSummary("Nội dung sẽ gửi Gemini cho một hồ sơ (đã che dữ liệu cá nhân); không gọi Gemini, không tốn lượt, không lưu")
            .WithMetadata(new RequestSizeLimitAttribute(PromptToolSupport.MaxRequestBytes))
            .WithDescription("Body JSON (UTF-8): { \"applicationId\": 5 } kèm \"systemPrompt\" (nháp) hoặc \"promptVersionId\"; " +
                             "không có cả hai thì dùng bản đang kích hoạt.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
        return app;
    }

    // ---------- A41: POST /api/prompts/assembled-preview ----------
    private static async Task<Results<Ok<AssembledPromptDto>, ProblemHttpResult>> AssembledPreviewAsync(
        HttpContext http, SmartCvDbContext db, IOptions<ScoringOptions> scoring, IOptions<GeminiOptions> gemini, CancellationToken ct)
    {
        var (request, bodyProblem) = await PromptToolSupport.ReadJsonBodyAsync<AssembledPreviewRequest>(http, ct);
        if (bodyProblem is not null) return bodyProblem;
        if (request is null)
            return Problem(400, "Thiếu nội dung yêu cầu",
                "Gửi JSON { \"applicationId\": 5 }, kèm systemPrompt (bản nháp) hoặc promptVersionId nếu không dùng bản đang kích hoạt.");
        if (request.ApplicationId <= 0)
            return Problem(400, "Mã hồ sơ không hợp lệ", "applicationId phải là số nguyên dương.");
        var invalid = PromptToolSupport.ValidateSource(request.SystemPrompt, request.PromptVersionId);
        if (invalid is not null) return invalid;

        // Fail-closed: cấu hình cũ (không che) sẽ gửi CV gốc cho Gemini; API xem trước không bao giờ trả CV chưa che.
        var g = gemini.Value;
        if (!g.MaskPersonalData)
            return TypedResults.Problem(title: "Chưa bật che dữ liệu cá nhân",
                detail: "Gemini:MaskPersonalData đang tắt nên nội dung gửi Gemini chứa dữ liệu cá nhân gốc. " +
                        "API xem trước không trả nội dung này; hãy bật lại che dữ liệu (mặc định).",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = "PersonalDataMaskingDisabled" });

        var (application, applicationProblem) = await PromptToolSupport.LoadReadyApplicationAsync(db, request.ApplicationId, ct);
        if (application is null) return applicationProblem!;
        var (prompt, promptProblem) = await PromptToolSupport.ResolvePromptAsync(db, request.SystemPrompt, request.PromptVersionId, ct);
        if (prompt is null) return promptProblem!;
        var job = await db.LoadJobProfileAsync(application.JobId, ct);
        if (job is null) return PromptToolSupport.JobNotFound(application.JobId);

        // Cùng đầu vào và cùng cấu hình với lượt chấm thật (GeminiEvaluationService) → cùng nội dung
        var opt = scoring.Value;
        var assembled = AiPromptAssembler.Assemble(
            new AiEvaluationRequest(opt.ModelName, opt.Temperature, prompt.SystemPrompt, job, application.ExtractedText, application.CandidateName),
            g.MaxCvCharacters, g.MaskPersonalData);
        var mask = assembled.Mask!;

        return TypedResults.Ok(new AssembledPromptDto
        {
            ApplicationId = application.ApplicationId, JobId = job.JobId, JobTitle = job.Title,
            PromptSource = prompt.Source, PromptVersionId = prompt.VersionId, PromptVersion = prompt.Name, IsDraft = prompt.IsDraft,
            Model = opt.ModelName, Temperature = opt.Temperature, AiSimulated = g.UseFake,
            SystemPrompt = assembled.SystemPrompt, UserPrompt = assembled.UserPrompt,
            PersonalDataMasked = assembled.PersonalDataMasked,
            MaskedNames = mask.Names, MaskedEmails = mask.Emails, MaskedPhones = mask.Phones, MaskedLinks = mask.Links,
            MaskedBirthDates = mask.BirthDates, MaskedGenders = mask.Genders, MaskedTotal = mask.Total,
            NameMaskSource = mask.NameSource.ToString(),
            CvCharacters = assembled.CvCharacters, CvCharactersSent = assembled.CvCharactersSent,
            MaxCvCharacters = g.MaxCvCharacters, Truncated = assembled.Truncated,
            AllowedSkillCodes = [.. assembled.AllowedSkillCodes]
        });
    }

    // ---------- GET /api/prompts ----------
    private static async Task<Ok<List<PromptVersionDto>>> ListAsync(SmartCvDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await LoadAsync(db, null, ct));

    // ---------- GET /api/prompts/active ----------
    private static async Task<Results<Ok<PromptVersionDto>, ProblemHttpResult>> ActiveAsync(SmartCvDbContext db, CancellationToken ct)
    {
        var dto = (await LoadAsync(db, p => p.IsActive, ct)).FirstOrDefault();
        return dto is null
            ? Problem(404, "Chưa có prompt đang dùng", "Hãy kích hoạt một phiên bản.")
            : TypedResults.Ok(dto);
    }

    // ---------- GET /api/prompts/{id} ----------
    private static async Task<Results<Ok<PromptVersionDto>, ProblemHttpResult>> GetAsync(int id, SmartCvDbContext db, CancellationToken ct)
    {
        var dto = (await LoadAsync(db, p => p.PromptVersionId == id, ct)).FirstOrDefault();
        return dto is null
            ? Problem(404, "Không tìm thấy", $"Không có phiên bản prompt #{id}.")
            : TypedResults.Ok(dto);
    }

    // ---------- POST /api/prompts ----------
    private static async Task<Results<Created<PromptVersionDto>, ProblemHttpResult>> CreateAsync(
        CreatePromptVersionRequest request, SmartCvDbContext db, ILogger<PromptVersion> logger, CancellationToken ct)
    {
        var name = (request.VersionName ?? string.Empty).Trim();
        var prompt = (request.SystemPrompt ?? string.Empty).Trim();
        var note = string.IsNullOrWhiteSpace(request.ChangeNote) ? null : request.ChangeNote.Trim();

        if (name.Length == 0) return Problem(400, "Thiếu tên phiên bản", "Hãy đặt tên, ví dụ v3-du-an-thuc-tap.");
        if (name.Length > 100) return Problem(400, "Tên quá dài", "Tên phiên bản tối đa 100 ký tự.");
        if (prompt.Length == 0) return Problem(400, "Thiếu nội dung", "Nội dung system prompt không được để trống.");
        if (prompt.Length > MaxPromptLength)
            return Problem(400, "Prompt quá dài", $"Tối đa {MaxPromptLength:N0} ký tự (đang có {prompt.Length:N0}).");
        if (note is { Length: > 500 }) return Problem(400, "Ghi chú quá dài", "Ghi chú thay đổi tối đa 500 ký tự.");

        // Tên không trùng (database cũng có UQ_PromptVersions_VersionName; kiểm trước để báo lỗi dễ hiểu)
        if (await db.PromptVersions.AnyAsync(p => p.VersionName == name, ct))
            return Problem(409, "Trùng tên", $"Đã có phiên bản tên \"{name}\".");

        // Nội dung giống hệt một bản cũ (chỉ khác khoảng trắng) → không tạo, tránh so sánh hai bản như nhau
        var key = Squash(prompt);
        var existing = await db.PromptVersions.AsNoTracking()
            .Select(p => new { p.VersionName, p.SystemPrompt })
            .ToListAsync(ct);
        var same = existing.FirstOrDefault(p => Squash(p.SystemPrompt) == key);
        if (same is not null)
            return Problem(409, "Nội dung không đổi", $"Nội dung giống hệt phiên bản \"{same.VersionName}\". Hãy sửa prompt trước khi lưu.");

        var entity = new PromptVersion
        {
            VersionName = name,
            SystemPrompt = prompt,
            ChangeNote = note,
            IsActive = false,          // tạo xong chưa dùng ngay: chạy đánh giá trước, tốt hơn mới kích hoạt
            CreatedAt = DateTime.UtcNow
        };
        db.PromptVersions.Add(entity);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            logger.LogWarning("Tên phiên bản prompt đã được một yêu cầu khác sử dụng.");
            return Problem(409, "Trùng tên", $"Đã có phiên bản tên \"{name}\".");
        }

        var dto = (await LoadAsync(db, p => p.PromptVersionId == entity.PromptVersionId, ct)).First();
        return TypedResults.Created($"/api/prompts/{entity.PromptVersionId}", dto);
    }

    // ---------- POST /api/prompts/{id}/activate ----------
    private static async Task<Results<Ok<PromptVersionDto>, ProblemHttpResult>> ActivateAsync(
        int id, SmartCvDbContext db, ILogger<PromptVersion> logger, CancellationToken ct)
    {
        if (!await db.PromptVersions.AnyAsync(p => p.PromptVersionId == id, ct))
            return Problem(404, "Không tìm thấy", $"Không có phiên bản prompt #{id}.");

        // Chỉ mục UX_PromptVersions_OneActive chỉ cho MỘT dòng IsActive = 1.
        // Vì vậy phải TẮT bản cũ trước rồi mới BẬT bản mới, hai câu lệnh trong cùng một giao dịch:
        // lỗi giữa chừng thì quay lại như cũ, không bao giờ có 0 hoặc 2 bản đang dùng.
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                await db.PromptVersions
                    .Where(p => p.IsActive && p.PromptVersionId != id)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false), ct);
                await db.PromptVersions
                    .Where(p => p.PromptVersionId == id && !p.IsActive)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, true), ct);
                await tx.CommitAsync(ct);
            });
        }
        // EF's execution strategy can wrap a transient SQL failure in InvalidOperationException.
        catch (Exception ex) when (ex.GetBaseException() is SqlException { Number: 1205 or 2601 or 2627 })
        {
            logger.LogWarning("Xung đột khi kích hoạt prompt #{Id}; giao dịch đã rollback.", id);
            return Problem(409, "Xung đột kích hoạt", "Một yêu cầu khác đang kích hoạt prompt. Hãy tải lại và thử lại.");
        }

        logger.LogInformation("Đã kích hoạt prompt #{Id}", id);
        var dto = (await LoadAsync(db, p => p.PromptVersionId == id, ct)).First();
        return TypedResults.Ok(dto);
    }

    // ---------- dùng chung ----------

    /// <summary>
    /// Phiên bản + HasRuns + tỷ lệ đúng của lần PromptTest XONG gần nhất.
    /// Lọc trên bảng TRƯỚC khi chọn cột → EF dịch thành một câu SQL (hai truy vấn con), không tải hết EvaluationRuns.
    /// </summary>
    private static async Task<List<PromptVersionDto>> LoadAsync(
        SmartCvDbContext db, Expression<Func<PromptVersion, bool>>? filter, CancellationToken ct)
    {
        var q = db.PromptVersions.AsNoTracking();
        if (filter is not null) q = q.Where(filter);

        var rows = await q
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.PromptVersionId)
            .Select(p => new
            {
                p.PromptVersionId, p.VersionName, p.SystemPrompt, p.ChangeNote, p.IsActive, p.CreatedAt,
                HasRuns = p.EvaluationRuns.Any(),
                LatestAccuracyPercent = p.EvaluationRuns
                    .Where(r => r.RunType == RunTypes.PromptTest && r.Status == RunStatuses.Completed)
                    .OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.RunId)
                    .Select(r => r.AccuracyPercent)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        return rows.Select(p => new PromptVersionDto
        {
            PromptVersionId = p.PromptVersionId,
            VersionName = p.VersionName,
            SystemPrompt = p.SystemPrompt,
            ChangeNote = p.ChangeNote,
            IsActive = p.IsActive,
            CreatedAt = DateTime.SpecifyKind(p.CreatedAt, DateTimeKind.Utc),
            HasRuns = p.HasRuns,
            LatestAccuracyPercent = p.LatestAccuracyPercent
        }).ToList();
    }

    /// <summary>Gom mọi khoảng trắng thành một dấu cách để so nội dung.</summary>
    private static string Squash(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static ProblemHttpResult Problem(int status, string title, string detail) =>
        TypedResults.Problem(title: title, detail: detail, statusCode: status);
}
