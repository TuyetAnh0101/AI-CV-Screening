using Microsoft.EntityFrameworkCore;
using SmartCv.Api.Auditing;
using SmartCv.Api.Auth;
using SmartCv.Api.Reports;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Ops;
using SmartCv.Core.Contracts;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// Read-only global management aggregates. Not under BusinessOperator, does not expose CV
/// or candidate details. All endpoints require a DB-verified SystemAdmin JWT.
/// </summary>
public static class AdminOperationsReportEndpoints
{
    public static IEndpointRouteBuilder MapAdminOperationsReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/reports")
            .WithTags("Admin Operations Reports")
            .RequireAuthorization(AuthPolicies.SystemAdmin)
            .AddEndpointFilter<AuditEndpointFilter>();
        group.MapGet("/summary", SummaryAsync).WithName("AdminOperationsReportSummary")
            .Produces<AdminReportSummaryDto>();
        group.MapGet("/trend", TrendAsync).WithName("AdminOperationsReportTrend")
            .Produces<AdminReportTrendDto>();
        group.MapGet("/exports", ExportsAsync).WithName("AdminOperationsReportExports")
            .Produces<AdminReportExportPageDto>();
        group.MapGet("/preview", PreviewAsync).WithName("AdminOperationsReportPreview")
            .Produces<AdminReportExportPreviewDto>();
        group.MapGet("/health", HealthAsync).WithName("AdminOperationsReportHealth")
            .Produces<AdminReportHealthDto>();
        group.MapGet("/download", DownloadAsync).WithName("AdminOperationsReportDownload")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv");
        return app;
    }

    private static IResult Invalid(string message) => Results.Problem(statusCode: 400,
        title: "Bộ lọc báo cáo quản trị không hợp lệ", detail: message,
        extensions: new Dictionary<string, object?> { ["code"] = "admin_report_invalid_filter" });

    private static IResult Unavailable() => Results.Problem(statusCode: 503,
        title: "Báo cáo quản trị tạm không khả dụng",
        detail: "Kiểm tra kết nối database và các migration đã được phê duyệt; không trả số liệu ước lượng.",
        extensions: new Dictionary<string, object?> { ["code"] = "admin_report_unavailable" });

    private static bool SqlProblem(Exception ex) => ex is DbUpdateException or
        Microsoft.Data.SqlClient.SqlException or System.Data.Common.DbException or
        InvalidOperationException; // query translation/provider state; never expose exception details

    private static async Task<IResult> SummaryAsync(HttpContext http, SmartCvDbContext db,
        TimeProvider time, ILoggerFactory loggers, CancellationToken ct)
    {
        var (filter, error) = AdminReportFilter.Parse(http.Request.Query, time.GetUtcNow());
        if (filter is null) return Invalid(error!);
        try { return Results.Ok(await AdminOperationsReportReader.SummaryAsync(db, filter, time.GetUtcNow().UtcDateTime, ct)); }
        catch (Exception ex) when (SqlProblem(ex)) { loggers.CreateLogger("SCApp.AdminReports").LogWarning("Admin report summary unavailable: {Type}", ex.GetType().Name); return Unavailable(); }
    }

    private static async Task<IResult> TrendAsync(HttpContext http, SmartCvDbContext db,
        TimeProvider time, ILoggerFactory loggers, CancellationToken ct)
    {
        var (filter, error) = AdminReportFilter.Parse(http.Request.Query, time.GetUtcNow());
        if (filter is null) return Invalid(error!);
        try { return Results.Ok(await AdminOperationsReportReader.TrendAsync(db, filter, time.GetUtcNow().UtcDateTime, ct)); }
        catch (Exception ex) when (SqlProblem(ex)) { loggers.CreateLogger("SCApp.AdminReports").LogWarning("Admin report trend unavailable: {Type}", ex.GetType().Name); return Unavailable(); }
    }

    private static async Task<IResult> ExportsAsync(HttpContext http, SmartCvDbContext db,
        TimeProvider time, ILoggerFactory loggers, CancellationToken ct)
    {
        var (filter, error) = AdminReportFilter.Parse(http.Request.Query, time.GetUtcNow(), paging: true);
        if (filter is null) return Invalid(error!);
        try { return Results.Ok(await AdminOperationsReportReader.ExportsAsync(db, filter, time.GetUtcNow().UtcDateTime, ct)); }
        catch (Exception ex) when (SqlProblem(ex)) { loggers.CreateLogger("SCApp.AdminReports").LogWarning("Admin report export activity unavailable: {Type}", ex.GetType().Name); return Unavailable(); }
    }

    private static async Task<IResult> PreviewAsync(HttpContext http, SmartCvDbContext db,
        TimeProvider time, ILoggerFactory loggers, CancellationToken ct)
    {
        var (filter, error) = AdminReportFilter.Parse(http.Request.Query, time.GetUtcNow());
        if (filter is null) return Invalid(error!);
        try
        {
            var count = await AdminOperationsReportReader.ExportCountAsync(db, filter, ct);
            var allowed = count <= AdminReportFilter.MaxDownloadEvents;
            return Results.Ok(new AdminReportExportPreviewDto(filter.ToDto(time.GetUtcNow().UtcDateTime),
                count, AdminReportFilter.MaxDownloadEvents, allowed,
                allowed ? null : "admin_report_too_many_events", "csv"));
        }
        catch (Exception ex) when (SqlProblem(ex)) { loggers.CreateLogger("SCApp.AdminReports").LogWarning("Admin report preview unavailable: {Type}", ex.GetType().Name); return Unavailable(); }
    }

    private static async Task<IResult> HealthAsync(HttpContext http, ApplicationReadiness readiness,
        IConfiguration config, TimeProvider time, CancellationToken ct)
    {
        if (http.Request.Query.Count != 0) return Invalid("Health không nhận bộ lọc thời gian.");
        var now = time.GetUtcNow().UtcDateTime;
        var health = await readiness.CheckAsync(ct);
        return Results.Ok(new AdminReportHealthDto(now, health.Status, health.Checks,
        [
            new("Scoring", config.GetValue<bool>("Scoring:WorkerEnabled"),
                "Cờ cấu hình, không phải heartbeat hay bằng chứng worker đang chạy."),
            new("Retention", config.GetValue<bool>("Retention:AutoAnonymize"),
                "Cờ cấu hình, không chứng minh job đã chạy thành công.")
        ], "not_monitored", "Email chỉ kiểm tra cấu hình, không kết nối thử SMTP.",
            "not_available_until_A62"));
    }

    private static async Task<IResult> DownloadAsync(HttpContext http, SmartCvDbContext db,
        AuditTrail audit, TimeProvider time, ILoggerFactory loggers, CancellationToken ct)
    {
        var (filter, error) = AdminReportFilter.Parse(http.Request.Query, time.GetUtcNow(), csv: true);
        if (filter is null) return Invalid(error!);
        try
        {
            var count = await AdminOperationsReportReader.ExportCountAsync(db, filter, ct);
            if (count > AdminReportFilter.MaxDownloadEvents)
                return Results.Problem(statusCode: 413, title: "Quá nhiều sự kiện xuất",
                    detail: "Thu hẹp thời gian để số sự kiện xuất không vượt 1000, tránh file không đầy đủ.",
                    extensions: new Dictionary<string, object?> { ["code"] = "admin_report_too_many_events" });
            var now = time.GetUtcNow().UtcDateTime;
            var summary = await AdminOperationsReportReader.SummaryAsync(db, filter, now, ct);
            var trend = await AdminOperationsReportReader.TrendAsync(db, filter, now, ct);
            var page = await AdminOperationsReportReader.ExportsAsync(db, filter, now, ct, allForDownload: true);
            if (page.Total > AdminReportFilter.MaxDownloadEvents || page.Items.Count != page.Total)
                return Results.Conflict(new { code = "admin_report_changed_during_export",
                    message = "Dữ liệu vừa thay đổi hoặc vượt giới hạn. Hãy xem trước lại." });
            var bytes = AdminOperationsReportCsvWriter.Write(summary, trend, page.Items);
            ct.ThrowIfCancellationRequested();
            // Audit is mandatory: file MUST NOT be returned before a successful SaveChanges.
            var evt = audit.Stage(db, "admin.operations_report_exported", null, http.User, outcome: "AccessGranted");
            evt.SafeSummary = $"Xuất CSV báo cáo vận hành; {filter.From:yyyy-MM-dd}..{filter.To:yyyy-MM-dd}; {page.Items.Count} sự kiện xuất; chỉ metadata";
            await db.SaveChangesAsync(ct);
            audit.ConfirmCommitted();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(bytes, AdminOperationsReportCsvWriter.ContentType,
                AdminOperationsReportCsvWriter.FileName(now));
        }
        catch (Exception ex) when (SqlProblem(ex))
        {
            loggers.CreateLogger("SCApp.AdminReports").LogWarning("Admin report download failed before file grant: {Type}", ex.GetType().Name);
            return Unavailable();
        }
    }
}
