using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Api.Auditing;
using SmartCv.Api.Auth;
using SmartCv.Core.Auditing;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Export;
using SmartCv.Core.Options;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Export;

namespace SmartCv.Api.Endpoints;

public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.GetRequiredService<IOptions<CvUploadEndpointOptions>>().Value.Enabled) return app;
        var group = app.MapGroup("/api/exports").WithTags("Export");
        group.MapGet("/settings", (HttpContext http, IOptions<ExportOptions> options) => Results.Ok(new ExportSettingsDto
            { MaxRows = options.Value.MaxRows, CanViewLogs = http.User.IsAdmin() })).WithName("ExportSettings");
        group.MapGet("/", ListAsync).RequireAuthorization(AuthPolicies.Admin).WithName("ListExports");
        app.MapGet("/api/runs/{id:int}/ranking/export", DownloadAsync).WithTags("Export").WithName("ExportRanking")
            .Produces(StatusCodes.Status200OK, contentType: ExcelRankingWriter.ContentType);
        app.MapGet("/api/runs/{id:int}/ranking/export/preview", PreviewAsync).WithTags("Export").WithName("PreviewRankingExport")
            .Produces<RankingExportPreviewDto>();
        return app;
    }

    private static Task<IResult> DownloadAsync(int id, HttpContext http, SmartCvDbContext db, IOptions<ReviewFlagOptions> flags,
        IOptions<ExportOptions> options, AuditTrail audit, TimeProvider clock, ILogger<ExportLog> logger, CancellationToken ct) =>
        RankingAsync(id, http, db, flags.Value, options.Value, audit, clock, logger, false, ct);
    private static Task<IResult> PreviewAsync(int id, HttpContext http, SmartCvDbContext db, IOptions<ReviewFlagOptions> flags,
        IOptions<ExportOptions> options, AuditTrail audit, TimeProvider clock, ILogger<ExportLog> logger, CancellationToken ct) =>
        RankingAsync(id, http, db, flags.Value, options.Value, audit, clock, logger, true, ct);

    private static async Task<IResult> RankingAsync(int id, HttpContext http, SmartCvDbContext db, ReviewFlagOptions flags,
        ExportOptions options, AuditTrail audit, TimeProvider clock, ILogger logger, bool preview, CancellationToken ct)
    {
        if (id <= 0) return Problem(400, "export_invalid_id", "Mã lượt chấm phải là số nguyên dương.");
        var (query, errors) = RankingExportQuery.Parse(Input(http));
        if (query is null) return Invalid(errors);
        if (query.Format == "pdf") return Problem(400, "export_pdf_disabled", "Chưa bật xuất PDF. Hãy chọn Excel.");
        try
        {
            // Stable read of scores, current HR stages, flags and skills, without requiring SNAPSHOT DB configuration.
            // A39's existing privacy middleware also holds its lease through the entire file response.
            await using var tx = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
            var (ranking, missing) = await RunEndpoints.BuildRankingAsync(id, db, flags, ct, query.JobId);
            if (ranking is null) return Problem(404, "export_not_found", missing!.Title);
            var rows = query.Apply(ranking.Items);
            var (code, reason) = Block(ranking, rows.Count, options.MaxRows);
            if (preview)
            {
                if (tx is not null) await tx.CommitAsync(ct);
                return Results.Ok(new RankingExportPreviewDto(id, ranking.JobId, ranking.RunStatus, ranking.Items.Count, rows.Count,
                    ranking.Items.Count(r => r.Stage == ApplicationStages.Shortlisted), options.MaxRows, query.Anonymous,
                    query.ScopeText(), code is null, code, reason));
            }
            if (code is not null) return Problem(code == "export_too_many_rows" ? 400 : 409, code, reason!);

            var ids = rows.Select(r => r.EvaluationId).ToArray();
            var skills = (await db.EvaluationSkills.AsNoTracking()
                .Where(s => ids.Contains(s.EvaluationId) && !s.IsMatched && s.JobSkill.SkillType == SkillTypes.MustHave)
                .Select(s => new { s.EvaluationId, s.JobSkill.DisplayName }).ToListAsync(ct))
                .GroupBy(s => s.EvaluationId).ToDictionary(g => g.Key,
                    g => string.Join(", ", g.Select(s => s.DisplayName).Distinct().OrderBy(s => s, StringComparer.Ordinal)));
            var now = clock.GetUtcNow().UtcDateTime;
            ct.ThrowIfCancellationRequested();
            var bytes = ExcelRankingWriter.Write(ranking, rows, skills, query, http.User.ActorName(), now);
            ct.ThrowIfCancellationRequested();
            // One transaction and one SaveChanges for A31 metadata + B43's verified actor/time event.
            var action = audit.Stage(db, "runs.ranking_exported", id.ToString(CultureInfo.InvariantCulture), http.User, outcome: "AccessGranted");
            db.ExportLogs.Add(new ExportLog
            {
                AuditEvent = action, RunId = id, JobId = ranking.JobId, Format = query.Format, Stage = query.Stage, Fit = query.Fit,
                SearchApplied = query.Search is not null, NeedsReview = query.NeedsReview, ManualFlags = query.ManualFlags,
                RowCount = rows.Count, Anonymized = query.Anonymous
            });
            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            audit.ConfirmCommitted();
            return Results.File(bytes, ExcelRankingWriter.ContentType, ExcelRankingWriter.FileName(id, ranking.JobId, now));
        }
        catch (Exception error) when (AuthProblems.IsDatabaseFailure(error))
        {
            // Do not log the exception, query/search, SQL values, candidate identity or connection details.
            AuditTrail.LogUnavailable(logger, "runs.ranking_exported", http.TraceIdentifier, error);
            return Problem(503, "export_unavailable", "Dịch vụ xuất tạm không khả dụng.",
                "Kiểm tra kết nối và migration ExportLogs/AuditEvents đã được phê duyệt. Chưa cấp file khi chưa ghi được nhật ký.");
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            logger.LogError("Ranking export failed; failure type {Type}.", error.GetType().Name);
            return Problem(500, "export_failed", "Chưa tạo được file xuất. Vui lòng thử lại sau.");
        }
    }

    private static (string? Code, string? Reason) Block(RankingResponse ranking, int count, int maxRows) =>
        ranking.RunStatus != RunStatuses.Completed || ranking.Items.Any(r => r.Status == EvaluationStatuses.Pending)
            ? ("export_run_not_completed", "Chỉ xuất khi lượt chấm đã hoàn tất. Hãy đợi rồi tải lại kết quả.")
        : count == 0 ? ("export_empty", "Không có hồ sơ trong phạm vi đã chọn. Hãy đổi bộ lọc.")
        : count > maxRows ? ("export_too_many_rows", $"Mỗi file tối đa {maxRows} hồ sơ. Hãy lọc bớt.") : (null, null);

    private static async Task<IResult> ListAsync(HttpContext http, SmartCvDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var input = Input(http);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "runId", "jobId", "actorUserId", "anonymous", "from", "to", "page", "pageSize" };
        var errors = input.Where(p => !allowed.Contains(p.Key)).ToDictionary(p => p.Key, _ => new[] { "Tham số không được hỗ trợ." });
        var shared = input.Where(p => p.Key is not ("runId" or "jobId" or "anonymous"))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var (filter, sharedErrors) = AuditQuery.Parse(shared, clock.GetUtcNow().UtcDateTime);
        foreach (var pair in sharedErrors) errors[pair.Key] = pair.Value;
        int? Id(string key)
        {
            if (!input.TryGetValue(key, out var values)) return null;
            if (values.Length == 1 && int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0) return value;
            errors[key] = ["Chỉ gửi một mã là số nguyên dương."]; return null;
        }
        var run = Id("runId"); var job = Id("jobId"); bool? anonymous = null;
        if (input.TryGetValue("anonymous", out var raw))
        {
            if (raw.Length == 1 && bool.TryParse(raw[0], out var value)) anonymous = value;
            else errors["anonymous"] = ["Chỉ gửi một giá trị true hoặc false."];
        }
        if (errors.Count > 0 || filter is null) return Invalid(errors);
        try
        {
            var query = db.ExportLogs.AsNoTracking().Where(e => e.AuditEvent.OccurredAtUtc >= filter.FromUtc && e.AuditEvent.OccurredAtUtc < filter.UntilUtc);
            if (run is { } runId) query = query.Where(e => e.RunId == runId);
            if (job is { } jobId) query = query.Where(e => e.JobId == jobId);
            if (filter.ActorUserId is { } actor) query = query.Where(e => e.AuditEvent.ActorUserId == actor);
            if (anonymous is { } anon) query = query.Where(e => e.Anonymized == anon);
            var total = await query.CountAsync(ct);
            var rows = await query.OrderByDescending(e => e.AuditEvent.OccurredAtUtc).ThenByDescending(e => e.ExportId)
                .Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize)
                .Join(db.Jobs, e => e.JobId, j => j.JobId, (e, j) => new ExportLogDto(e.ExportId, e.AuditEventId,
                    e.RunId, e.JobId, j.Title, e.Format, e.Stage, e.Fit, e.SearchApplied, e.NeedsReview, e.ManualFlags,
                    e.RowCount, e.Anonymized, e.AuditEvent.ActorUserId, e.AuditEvent.ActorNameSnapshot ?? "Không rõ",
                    DateTime.SpecifyKind(e.AuditEvent.OccurredAtUtc, DateTimeKind.Utc))).ToListAsync(ct);
            return Results.Ok(new ExportLogPageDto(filter.Page, filter.PageSize, total,
                (int)Math.Ceiling(total / (double)filter.PageSize), rows));
        }
        catch (Exception error) when (AuthProblems.IsDatabaseFailure(error))
        { return Problem(503, "export_unavailable", "Nhật ký xuất tạm không khả dụng. Kiểm tra migration ExportLogs/AuditEvents đã được phê duyệt."); }
    }

    private static Dictionary<string, string[]> Input(HttpContext http) => http.Request.Query
        .ToDictionary(p => p.Key, p => p.Value.Select(v => v ?? "").ToArray(), StringComparer.OrdinalIgnoreCase);
    private static IResult Invalid(Dictionary<string, string[]> errors) => Results.ValidationProblem(errors,
        title: "Tham số không hợp lệ", extensions: new Dictionary<string, object?> { ["code"] = "export_invalid_filter" });
    private static IResult Problem(int status, string code, string title, string? detail = null) =>
        Results.Problem(statusCode: status, title: title, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
