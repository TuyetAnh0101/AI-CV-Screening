using SmartCv.Core.Backup;

namespace SmartCv.Web.Services;

public sealed class AdminBackupApiClient(HttpClient http, ILogger<AdminBackupApiClient> logger) : ApiClientBase(http, logger)
{
    private const string Base = "api/admin/backups";

    public Task<ApiResult<BackupOverviewDto>> GetOverviewAsync(CancellationToken ct = default) => GetAsync<BackupOverviewDto>(Base + "/overview", ct);
    public Task<ApiResult<List<BackupPointDto>>> GetPointsAsync(CancellationToken ct = default) => GetAsync<List<BackupPointDto>>(Base + "/points", ct);

    // type is one of the fixed job types; each has its own audited route.
    public Task<ApiResult<BackupJobAccepted>> RequestAsync(string type, CancellationToken ct = default) =>
        PostJsonAsync<BackupJobAccepted>(Base + "/jobs/" + type, new { }, ct);

    public Task<ApiResult<BackupJobAccepted>> RecoverAsync(BackupRecoveryRequest request, CancellationToken ct = default) =>
        PostJsonAsync<BackupJobAccepted>(Base + "/recovery", request, ct);

    public Task<ApiResult<BackupAck>> SaveSettingsAsync(BackupSettingsDto settings, CancellationToken ct = default) =>
        PutJsonAsync<BackupAck>(Base + "/settings", settings, ct);

    public Task<ApiResult<BackupDriveConnectStart>> ConnectDriveAsync(CancellationToken ct = default) =>
        PostJsonAsync<BackupDriveConnectStart>(Base + "/drive/connect", new { }, ct);
    public Task<ApiResult<BackupAck>> CheckDriveAsync(CancellationToken ct = default) => PostJsonAsync<BackupAck>(Base + "/drive/check", new { }, ct);
    public Task<ApiResult<BackupAck>> DisconnectDriveAsync(CancellationToken ct = default) => DeleteJsonAsync<BackupAck>(Base + "/drive", ct);

    public Task<ApiResult<BackupReportDto>> GetReportAsync(BackupReportFilter filter, CancellationToken ct = default) =>
        GetAsync<BackupReportDto>(Base + "/report" + Query(filter), ct);
    public Task<ApiResult<DownloadedFile>> ExportReportAsync(BackupReportFilter filter, CancellationToken ct = default) =>
        GetFileAsync(Base + "/report/export" + Query(filter), ct);

    private static string Query(BackupReportFilter f) => $"?from={f.From:yyyy-MM-dd}&to={f.To:yyyy-MM-dd}&status={f.Status}&type={f.Type}&trigger={f.Trigger}";
}
