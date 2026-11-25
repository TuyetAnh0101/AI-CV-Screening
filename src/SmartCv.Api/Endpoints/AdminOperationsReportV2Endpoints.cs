using Microsoft.EntityFrameworkCore;
using SmartCv.Api.Auditing;
using SmartCv.Api.Auth;
using SmartCv.Api.Reports;
using SmartCv.Core.Contracts;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Export;
using SmartCv.Infrastructure.Ops;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// Additive, backwards-compatible Admin-only export v2. Original /api/admin/reports/* remains unchanged.
/// Never queries candidate/CV/prompts/score data. The writer receives aggregate DTOs only.
/// </summary>
public static class AdminOperationsReportV2Endpoints
{
    public static IEndpointRouteBuilder MapAdminOperationsReportV2Endpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/reports/v2")
            .RequireAuthorization(AuthPolicies.SystemAdmin)
            .WithTags("Admin Operations Reports V2")
            .AddEndpointFilter<AuditEndpointFilter>();
        group.MapGet("/preview", PreviewAsync).WithName("AdminReportV2Preview")
            .Produces<AdminReportV2PreviewDto>();
        group.MapGet("/download", DownloadAsync).WithName("AdminReportV2Download")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream");
        return app;
    }

    private static IResult Invalid(string? problem) => Results.Problem(
        statusCode: 400, title: "Bộ lọc báo cáo không hợp lệ",
        detail: problem ?? "Kiểm tra lại tham số báo cáo.",
        extensions: new Dictionary<string, object?> { ["code"] = "admin_report_v2_invalid_filter" });

    private static IResult Unavailable() => Results.Problem(statusCode: 503,
        title: "Báo cáo quản trị tạm thời không khả dụng",
        detail: "Không sử dụng số liệu thay thế. Kiểm tra database và nguồn telemetry đã triển khai.",
        extensions: new Dictionary<string, object?> { ["code"] = "admin_report_v2_unavailable" });

    private static IResult TooMany() => Results.Problem(statusCode: 413,
        title: "Nhật ký xuất vượt giới hạn báo cáo",
        detail: "Hãy thu hẹp khoảng ngày hoặc chọn báo cáo khác. Không cắt bỏ âm thầm sự kiện.",
        extensions: new Dictionary<string, object?> { ["code"] = "admin_report_too_many_events" });

    private static bool DatabaseProblem(Exception ex) =>
        ex is DbUpdateException or System.Data.Common.DbException or InvalidOperationException;

    private static async Task<IResult> PreviewAsync(HttpContext http, SmartCvDbContext db,
        TimeProvider time, ILoggerFactory loggers, CancellationToken ct)
    {
        var clock = time.GetUtcNow();
        var (request, error) = AdminReportV2Filter.Parse(http.Request.Query, clock);
        if (request is null) return Invalid(error);
        try
        {
            long? count = request.IncludeExportJournal
                ? await AdminOperationsReportReader.ExportCountAsync(db, request.Period, ct)
                : null;
            var allowed = count is null || count.Value <= AdminReportFilter.MaxDownloadEvents;
            var notices = new List<string>
            {
                "Chỉ số Users/Workspaces là trạng thái hiện tại, không phải lịch sử trong kỳ.",
                "Các truy vấn thống kê không nằm trong một transaction snapshot; dữ liệu có thể thay đổi khi xuất.",
                "Quota là đơn vị giữ ròng từ ledger, không phải token/chi phí nhà cung cấp.",
                "Quota bị từ chối, uptime và backup có thể không có nguồn kiểm chứng."
            };
            if (request.IncludeExportJournal)
                notices.Add("Nhật ký xuất được giới hạn 1.000 sự kiện; không tự cắt ngắn file.");
            return Results.Ok(new AdminReportV2PreviewDto(
                request.Period.ToDto(clock.UtcDateTime), request.Report, request.Format,
                AdminReportV2Filter.IncludedSections(request.Report), count,
                AdminReportFilter.MaxDownloadEvents, allowed,
                allowed ? null : "admin_report_too_many_events", notices));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (DatabaseProblem(ex))
        {
            loggers.CreateLogger("SCApp.AdminReportsV2").LogWarning("Report v2 preview unavailable: {Type}", ex.GetType().Name);
            return Unavailable();
        }
    }

    private static async Task<IResult> DownloadAsync(HttpContext http, SmartCvDbContext db,
        AuditTrail audit, ApplicationReadiness readiness, IConfiguration config,
        TimeProvider time, ILoggerFactory loggers, CancellationToken ct)
    {
        var instant = time.GetUtcNow();
        var (request, error) = AdminReportV2Filter.Parse(http.Request.Query, instant);
        if (request is null) return Invalid(error);
        try
        {
            if (request.IncludeExportJournal &&
                await AdminOperationsReportReader.ExportCountAsync(db, request.Period, ct)
                    > AdminReportFilter.MaxDownloadEvents) return TooMany();

            // Freeze reporting timestamp. Snapshot totals remain explicitly labelled as current.
            var now = instant.UtcDateTime;
            var current = await AdminOperationsReportReader.SummaryAsync(db, request.Period, now, ct);
            var trend = await AdminOperationsReportReader.TrendAsync(db, request.Period, now, ct);
            var period = request.Period;
            var priorPeriod = period with
            {
                From = period.From.AddDays(-period.Days),
                To = period.From.AddDays(-1),
                FromUtc = period.FromUtc.AddDays(-period.Days),
                UntilUtc = period.FromUtc
            };
            // Historical snapshot values in this response must NOT be compared. Writer compares events-in-period ONLY.
            var previous = await AdminOperationsReportReader.SummaryAsync(db, priorPeriod, now, ct);

            IReadOnlyList<AdminReportExportEventDto> journal = [];
            if (request.IncludeExportJournal)
            {
                var page = await AdminOperationsReportReader.ExportsAsync(
                    db, period, now, ct, allForDownload: true);
                if (page.Total > AdminReportFilter.MaxDownloadEvents || page.Items.Count != page.Total)
                    return Results.Conflict(new { code = "admin_report_changed_during_export",
                        message = "Nhật ký vừa thay đổi. Hãy xem trước và thử lại." });
                journal = page.Items;
            }
            AdminReportHealthDto? health = null;
            if (request.IncludeHealth)
            {
                var snapshot = await readiness.CheckAsync(ct);
                health = new(now, snapshot.Status, snapshot.Checks,
                [
                    new("Scoring", config.GetValue<bool>("Scoring:WorkerEnabled"),
                        "Chỉ cờ cấu hình, không phải heartbeat."),
                    new("Retention", config.GetValue<bool>("Retention:AutoAnonymize"),
                        "Chỉ cờ cấu hình, không chứng minh job đã chạy.")
                ], "not_monitored", "Email chỉ được kiểm tra cấu hình, không thử SMTP.",
                "not_available_until_A62");
            }

            var bytes = request.Format == "xlsx"
                ? AdminOperationsReportExcelWriter.Write(current, previous, trend,
                    journal, health, request.Report, now)
                : AdminOperationsReportV2CsvWriter.Write(current, previous, trend,
                    journal, health, request.Report);
            var contentType = request.Format == "xlsx"
                ? AdminOperationsReportExcelWriter.ContentType
                : AdminOperationsReportV2CsvWriter.ContentType;
            var fileName = request.Format == "xlsx"
                ? AdminOperationsReportExcelWriter.FileName(now, request.Report)
                : AdminOperationsReportV2CsvWriter.FileName(now, request.Report);
            ct.ThrowIfCancellationRequested();

            // Fail closed. Never return bytes unless an append-only audit event is committed.
            var evt = audit.Stage(db, "admin.operations_report_exported", null,
                http.User, outcome: "AccessGranted");
            evt.SafeSummary = $"Admin report v2 {request.Report}/{request.Format}; {period.From:yyyy-MM-dd}..{period.To:yyyy-MM-dd}; export-events={journal.Count}; metadata only";
            await db.SaveChangesAsync(ct);
            audit.ConfirmCommitted();
            http.Response.Headers["Cache-Control"] = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(bytes, contentType, fileName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (DatabaseProblem(ex))
        {
            loggers.CreateLogger("SCApp.AdminReportsV2").LogWarning("Report v2 download failed before grant: {Type}", ex.GetType().Name);
            return Unavailable();
        }
    }
}
