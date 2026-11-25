using System.Data;
using Microsoft.EntityFrameworkCore;
using SmartCv.Api.Auditing;
using SmartCv.Api.Auth;
using SmartCv.Api.Reports;
using SmartCv.Core.Contracts;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Export;

namespace SmartCv.Api.Endpoints;

/// <summary>New aggregate HR report. Existing /api/runs/{id}/ranking/export remains unchanged.</summary>
public static class HrRecruitmentReportEndpoints
{
    public static IEndpointRouteBuilder MapHrRecruitmentReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/hr/reports").WithTags("HR Reports")
            .RequireAuthorization(AuthPolicies.BusinessOperator)
            .AddEndpointFilter(DatabaseFailureFilterAsync);
        group.MapGet("/summary", SummaryAsync).WithName("HrRecruitmentReportSummary");
        group.MapGet("/rows", RowsAsync).WithName("HrRecruitmentReportRows");
        group.MapGet("/preview", PreviewAsync).WithName("HrRecruitmentReportPreview");
        group.MapGet("/export", ExportAsync).WithName("HrRecruitmentReportExcel");
        group.MapGet("/export/pdf", ExportPdfAsync).WithName("HrRecruitmentReportPdf");
        return app;
    }

    private static async Task<IResult> SummaryAsync(HttpRequest request, SmartCvDbContext db, CancellationToken ct)
    {
        var (filter, invalid) = HrReportFilter.Parse(request.Query);
        if (filter is null) return Problem(400, "Bộ lọc không hợp lệ", invalid);
        var (rows, status, error) = await HrReportReader.ReadAsync(db, filter, ct);
        if (rows is null) return Problem(status, "Không thể tạo báo cáo", error);
        return Results.Ok(HrReportRollup.Summary(rows));
    }

    private static async Task<IResult> RowsAsync(HttpRequest request, SmartCvDbContext db, CancellationToken ct)
    {
        var (filter, invalid) = HrReportFilter.Parse(request.Query);
        if (filter is null) return Problem(400, "Bộ lọc không hợp lệ", invalid);
        var (rows, status, error) = await HrReportReader.ReadAsync(db, filter, ct);
        if (rows is null) return Problem(status, "Không thể tải bảng", error);
        var count = rows.Count;
        var ordered = HrReportSort.Order(rows, filter.Sort, filter.Direction == "desc");
        var items = ordered.Skip((long)(filter.Page - 1) * filter.PageSize > count ? count : (filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize).Select(r => HrReportRollup.Row(r, filter.Anonymous)).ToArray();
        return Results.Ok(new HrReportPageDto(filter.Page, filter.PageSize, count,
            (int)Math.Ceiling((decimal)count / filter.PageSize), filter.Anonymous, items));
    }

    private static async Task<IResult> PreviewAsync(HttpRequest request, SmartCvDbContext db, CancellationToken ct)
    {
        var (filter, invalid) = HrReportFilter.Parse(request.Query);
        if (filter is null) return Problem(400, "Bộ lọc không hợp lệ", invalid);
        var (rows, status, error) = await HrReportReader.ReadAsync(db, filter, ct);
        if (rows is null) return Problem(status, "Không thể xem trước báo cáo", error);
        var ok = rows.Count <= HrReportFilter.MaxExportRows;
        return Results.Ok(new HrReportPreviewDto(rows.Count, HrReportFilter.MaxExportRows, filter.Anonymous, ok,
            ok ? null : "report_export_row_limit",
            ok ? null : $"Báo cáo có {rows.Count} dòng; tối đa {HrReportFilter.MaxExportRows}. Hãy lọc Job hoặc thời gian.", filter.Scope));
    }

    private static async Task<IResult> ExportAsync(HttpRequest request, HttpContext http,
        SmartCvDbContext db, AuditTrail audit, TimeProvider time, CancellationToken ct)
    {
        var (filter, invalid) = HrReportFilter.Parse(request.Query);
        if (filter is null) return Problem(400, "Bộ lọc không hợp lệ", invalid);
        // Stable snapshot across report queries and the mandatory append-only audit event.
        await using var tx = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        var (rows, status, error) = await HrReportReader.ReadAsync(db, filter, ct);
        if (rows is null) return Problem(status, "Không thể xuất báo cáo", error);
        if (rows.Count > HrReportFilter.MaxExportRows)
            return Problem(413, "Báo cáo vượt giới hạn", $"Tối đa {HrReportFilter.MaxExportRows} dòng. Thu hẹp Job hoặc thời gian.");
        var summary = HrReportRollup.Summary(rows);
        var ordered = HrReportSort.Order(rows, filter.Sort, filter.Direction == "desc");
        var detail = ordered.Select(x => HrReportRollup.Row(x, filter.Anonymous)).ToArray();
        var now = time.GetUtcNow().UtcDateTime;
        var exporter = http.User.FindFirst("name")?.Value;
        if (string.IsNullOrWhiteSpace(exporter)) return Problem(403, "Không xác định người xuất", "Cần phiên HR hợp lệ để xuất.");
        var file = HrRecruitmentReportExcelWriter.Write(summary, detail, filter.Scope, filter.Anonymous, now, exporter);
        // Do not use ExportLogs: that schema requires a single RunId+JobId, not a multi-job cohort.
        var excelAudit = audit.Stage(db, "hr.recruitment_report_exported", null, http.User, outcome: "AccessGranted");
        excelAudit.SafeSummary = AuditSummary("Excel", rows.Count, filter);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        audit.ConfirmCommitted();
        return Results.File(file, HrRecruitmentReportExcelWriter.ContentType,
            HrRecruitmentReportExcelWriter.Filename(now));
    }

    /// <summary>PDF of the same authorized cohort and scope as Excel; a separate audited export action.</summary>
    private static async Task<IResult> ExportPdfAsync(HttpRequest request, HttpContext http,
        SmartCvDbContext db, AuditTrail audit, TimeProvider time, CancellationToken ct)
    {
        var (filter, invalid) = HrReportFilter.Parse(request.Query);
        if (filter is null) return Problem(400, "Bộ lọc không hợp lệ", invalid);
        await using var tx = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        var (rows, status, error) = await HrReportReader.ReadAsync(db, filter, ct);
        if (rows is null) return Problem(status, "Không thể xuất PDF", error);
        if (rows.Count > HrReportFilter.MaxExportRows)
            return Problem(413, "Báo cáo vượt giới hạn", $"Tối đa {HrReportFilter.MaxExportRows} dòng. Thu hẹp Job hoặc thời gian.");
        var ordered = HrReportSort.Order(rows, filter.Sort, filter.Direction == "desc");
        var detail = ordered.Select(x => HrReportRollup.Row(x, filter.Anonymous)).ToArray();
        var now = time.GetUtcNow().UtcDateTime;
        var actor = http.User.FindFirst("name")?.Value;
        if (string.IsNullOrWhiteSpace(actor)) return Problem(403, "Không xác định người xuất", "Cần phiên HR hợp lệ để xuất.");
        // Render before writing the mandatory audit event; never report success for a failed PDF.
        var bytes = HrRecruitmentReportPdfWriter.Write(HrReportRollup.Summary(rows), detail,
            filter.Scope, filter.Anonymous, actor, now);
        var pdfAudit = audit.Stage(db, "hr.recruitment_report_pdf_exported", null, http.User, outcome: "AccessGranted");
        pdfAudit.SafeSummary = AuditSummary("PDF", rows.Count, filter);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        audit.ConfirmCommitted();
        return Results.File(bytes, HrRecruitmentReportPdfWriter.ContentType,
            HrRecruitmentReportPdfWriter.Filename(now));
    }

    // Sanitized, bounded, payload-free details; AuditEvents.SafeSummary is nvarchar(300).
    private static string AuditSummary(string format, int rows, HrReportFilter filter) =>
        $"Xuất {format} tuyển dụng; {rows} Application; Job={filter.JobId?.ToString() ?? "all"}; " +
        $"Run={filter.RunId?.ToString() ?? "latest"}; Stage={filter.Stage}; Eval={filter.Evaluation}; " +
        $"Ẩn danh={filter.Anonymous}; Từ={filter.Period.From?.ToString("yyyy-MM-dd") ?? "all"}; " +
        $"Đến={filter.Period.To?.ToString("yyyy-MM-dd") ?? "all"}";

    /// <summary>
    /// B57: a transient database failure is a controlled 503 (same contract as the Admin reports), never an
    /// unhandled 500 and never partial statistics. The transaction is rolled back by disposal, so no file and no
    /// "exported" audit event is produced. Exception text (SQL, values) is not returned or logged.
    /// </summary>
    private static async ValueTask<object?> DatabaseFailureFilterAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (Exception error) when (AuthProblems.IsDatabaseFailure(error) && !context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SCApp.HrReports")
                .LogWarning("HR report unavailable: {Type}", error.GetType().Name);
            return Problem(503, "Báo cáo tuyển dụng tạm thời không khả dụng",
                "Không đọc được dữ liệu báo cáo lúc này; không hiển thị số liệu ước lượng. Vui lòng thử lại sau.");
        }
    }

    private static IResult Problem(int status, string title, string? detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = "hr_report_unavailable" });
}
