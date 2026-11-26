using System.Globalization;
using System.Security.Claims;
using SmartCv.Api.Auditing;
using SmartCv.Api.Auth;
using SmartCv.Api.Backup;
using SmartCv.Core.Auth;
using SmartCv.Core.Backup;
using SmartCv.Infrastructure.Backup;
using SmartCv.Infrastructure.Export;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// Admin only. The API reads the backup catalog, queues fixed job types and manages the Drive connection. SQL
/// BACKUP/RESTORE, file access and uploads happen only in the separate worker process.
/// </summary>
public static class AdminBackupEndpoints
{
    public const string RecoveryConfirmation = "KHÔI PHỤC";

    public static IEndpointRouteBuilder MapAdminBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/backups").RequireAuthorization(AuthPolicies.Admin)
            .WithTags("Admin Backups").AddEndpointFilter<AuditEndpointFilter>();

        group.MapGet("/overview", async (HttpContext http, BackupAdminService service, GoogleDriveConnector drive, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.OverviewAsync(drive.Settings() is not null, ct));
        }).WithName("AdminBackupOverview").Produces<BackupOverviewDto>();

        group.MapGet("/points", async (BackupAdminService service, CancellationToken ct) => Results.Ok(await service.PointsAsync(ct)))
            .WithName("AdminBackupPoints");

        foreach (var type in new[] { BackupJobTypes.Full, BackupJobTypes.Log, BackupJobTypes.Verify, BackupJobTypes.RestoreDrill, BackupJobTypes.CatalogSync })
            group.MapPost("/jobs/" + type, (ClaimsPrincipal user, BackupAdminService service, GoogleDriveConnector drive, CancellationToken ct) =>
                SubmitAsync(type, user, service, drive, ct)).WithName("AdminBackupJob" + type.Replace("-", ""));

        group.MapPost("/recovery", async (BackupRecoveryRequest request, ClaimsPrincipal user, BackupAdminService service, GoogleDriveConnector drive, CancellationToken ct) =>
        {
            if (request.Confirmation?.Trim() != RecoveryConfirmation)
                return Problem(400, "backup_recovery_unconfirmed", "Chưa xác nhận", $"Nhập đúng \"{RecoveryConfirmation}\" để yêu cầu khôi phục.");
            return await SubmitAsync(BackupJobTypes.Recovery, user, service, drive, ct, request.PointId, request.StopAtServerTime);
        }).WithName("AdminBackupRecovery");

        group.MapPut("/settings", async (BackupSettingsDto settings, ClaimsPrincipal user, BackupAdminService service, CancellationToken ct) =>
        {
            if (user.GetUserId() is not { } userId) return Results.Unauthorized();
            var (ok, detail) = await service.UpdateSettingsAsync(settings, userId, ct);
            return ok ? Results.Ok(new BackupAck(true)) : Problem(400, "backup_settings_invalid", "Cấu hình không hợp lệ", detail);
        }).WithName("AdminBackupSettings");

        group.MapPost("/drive/connect", (ClaimsPrincipal user, GoogleDriveConnector drive, HttpContext http) =>
        {
            if (user.GetUserId() is not { } userId) return Results.Unauthorized();
            if (drive.Settings() is null)
                return Problem(409, "backup_drive_not_configured", "Chưa cấu hình Google Drive", "Máy chủ chưa có OAuth Client ID/Secret cho Google Drive.");
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new BackupDriveConnectStart(drive.Start(userId, http.Request)));
        }).WithName("AdminBackupDriveConnect");

        group.MapPost("/drive/check", async (GoogleDriveConnector drive, CancellationToken ct) =>
        {
            var (ok, error) = await drive.CheckAsync(ct);
            return ok ? Results.Ok(new BackupAck(true)) : Problem(409, "backup_drive_check_failed", "Kiểm tra Drive thất bại", BackupLabels.Error(error));
        }).WithName("AdminBackupDriveCheck");

        group.MapDelete("/drive", async (GoogleDriveConnector drive, CancellationToken ct) =>
            await drive.DisconnectAsync(ct) ? Results.Ok(new BackupAck(true)) : Results.NotFound()).WithName("AdminBackupDriveDisconnect");

        group.MapGet("/report", async (HttpContext http, BackupAdminService service, GoogleDriveConnector drive, TimeProvider time, CancellationToken ct) =>
        {
            if (Parse(http.Request.Query, time) is not { } filter) return InvalidFilter();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await BuildAsync(filter, service, drive, time, ct));
        }).WithName("AdminBackupReport").Produces<BackupReportDto>();

        group.MapGet("/report/export", async (HttpContext http, BackupAdminService service, GoogleDriveConnector drive, TimeProvider time, CancellationToken ct) =>
        {
            if (Parse(http.Request.Query, time) is not { } filter) return InvalidFilter();
            var report = await BuildAsync(filter, service, drive, time, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(BackupReportExcelWriter.Write(report), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"scapp-backup-{filter.From:yyyyMMdd}-{filter.To:yyyyMMdd}.xlsx");
        }).WithName("AdminBackupReportExport");

        // Google redirects the Admin's browser here: no bearer token. Authorization comes from the sealed state.
        app.MapGet(GoogleDriveConnector.CallbackPath, async (HttpContext http, GoogleDriveConnector drive, AuditTrail audit, IConfiguration config, CancellationToken ct) =>
        {
            var query = http.Request.Query;
            var (userId, error) = await drive.CompleteAsync(query["code"].ToString(), query["state"].ToString(), ct);
            if (query["error"].Count > 0) error ??= "access_denied";
            await audit.RecordAsync(AuditCatalog.ByCode("backup.drive_connected"), null, error is null ? "Succeeded" : "Denied",
                error is null ? null : Truncate(error), ct);
            var web = config["App:WebBaseUrl"];
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            var result = error is null ? "connected" : "failed-" + Uri.EscapeDataString(error);
            return string.IsNullOrWhiteSpace(web) ? Results.Text("Google Drive: " + result) : Results.Redirect(web.TrimEnd('/') + "/admin/backups?drive=" + result);
        }).AllowAnonymous().WithName("AdminBackupDriveCallback");
        return app;
    }

    private static string Truncate(string value) => value.Length <= 64 ? value : value[..64];

    private static async Task<IResult> SubmitAsync(string type, ClaimsPrincipal user, BackupAdminService service, GoogleDriveConnector drive,
        CancellationToken ct, Guid? point = null, DateTime? stopAt = null)
    {
        if (user.GetUserId() is not { } userId) return Results.Unauthorized();
        var (outcome, id, detail) = await service.RequestAsync(type, userId, drive.Settings() is not null, ct, point, stopAt);
        return outcome switch
        {
            BackupRequestOutcome.Accepted => Results.Accepted("/api/admin/backups/overview", new BackupJobAccepted(id!.Value, type)),
            BackupRequestOutcome.AlreadyPending => Problem(409, "backup_job_pending", "Yêu cầu đang được xử lý", detail),
            BackupRequestOutcome.Blocked => Problem(409, "backup_not_ready", "Chưa thể thực hiện", detail),
            BackupRequestOutcome.NotFound => Problem(404, "backup_point_not_found", "Không tìm thấy", detail),
            _ => Problem(400, "backup_request_invalid", "Yêu cầu không hợp lệ", detail)
        };
    }

    private static IResult Problem(int status, string code, string title, string? detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });

    private static async Task<BackupReportDto> BuildAsync(BackupReportFilter filter, BackupAdminService service, GoogleDriveConnector drive, TimeProvider time, CancellationToken ct) =>
        BackupReportBuilder.Build(await service.JobsAsync(filter.From, filter.To, ct), await service.OverviewAsync(drive.Settings() is not null, ct), filter, time.GetUtcNow().UtcDateTime);

    private static IResult InvalidFilter() => Problem(400, "backup_report_invalid_filter", "Bộ lọc không hợp lệ", "Chọn khoảng ngày tối đa 366 ngày và giá trị lọc có trong danh sách.");

    private static BackupReportFilter? Parse(IQueryCollection query, TimeProvider time)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime.AddHours(7));
        bool Day(string key, DateOnly fallback, out DateOnly value)
        {
            value = fallback;
            return query[key].Count == 0 || DateOnly.TryParseExact(query[key].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        }
        string? Choice(string key) => query[key].Count == 0 || query[key].ToString().Length == 0 ? null : query[key].ToString();
        if (!Day("from", today.AddDays(-29), out var from) || !Day("to", today, out var to) || from > to || to.DayNumber - from.DayNumber > 365) return null;
        var status = Choice("status"); var type = Choice("type"); var trigger = Choice("trigger");
        if ((status is not null && !BackupJobStatuses.All.Contains(status)) || (type is not null && !BackupJobTypes.All.Contains(type))
            || trigger is not (null or "automatic" or "manual")) return null;
        return new(from, to, status, type, trigger);
    }
}
