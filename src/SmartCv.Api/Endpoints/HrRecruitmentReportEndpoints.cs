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
            .RequireAuthorization(AuthPolicies.BusinessOperator);
        group.MapGet("/summary", SummaryAsync).WithName("HrRecruitmentReportSummary");
        group.MapGet("/rows", RowsAsync).WithName("HrRecruitmentReportRows");
        group.MapGet("/preview", PreviewAsync).WithName("HrRecruitmentReportPreview");
        group.MapGet("/export", ExportAsync).WithName("HrRecruitmentReportExcel");
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
        var items = rows.Skip((long)(filter.Page - 1) * filter.PageSize > count ? count : (filter.Page - 1) * filter.PageSize)
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
        var detail = rows.Select(x => HrReportRollup.Row(x, filter.Anonymous)).ToArray();
        var now = time.GetUtcNow().UtcDateTime;
        var file = HrRecruitmentReportExcelWriter.Write(summary, detail, filter.Scope, filter.Anonymous, now);
        // Do not use ExportLogs: that schema requires a single RunId+JobId, not a multi-job cohort.
        audit.Stage(db, "hr.recruitment_report_exported", null, http.User, outcome: "AccessGranted");
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        audit.ConfirmCommitted();
        return Results.File(file, HrRecruitmentReportExcelWriter.ContentType,
            HrRecruitmentReportExcelWriter.Filename(now));
    }

    private static IResult Problem(int status, string title, string? detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = "hr_report_unavailable" });
}
