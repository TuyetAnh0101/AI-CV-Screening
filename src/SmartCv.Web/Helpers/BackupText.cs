using System.Globalization;
using SmartCv.Core.Backup;

namespace SmartCv.Web.Helpers;

/// <summary>Presentation of backup data. States only what the server reported.</summary>
public static class BackupText
{
    public static string Time(DateTime? utc) => utc is { } value
        ? value.AddHours(7).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) + " (UTC+7)" : "Chưa ghi nhận";

    public static string ServerTime(DateTime? local) => local is { } value ? value.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture) : "—";

    public static string Size(long? bytes) => bytes switch
    {
        null => "—",
        < 1024 => bytes + " B",
        < 1048576 => (bytes.Value / 1024.0).ToString("0.#", CultureInfo.GetCultureInfo("vi-VN")) + " KB",
        < 1073741824 => (bytes.Value / 1048576.0).ToString("0.#", CultureInfo.GetCultureInfo("vi-VN")) + " MB",
        _ => (bytes.Value / 1073741824.0).ToString("0.##", CultureInfo.GetCultureInfo("vi-VN")) + " GB"
    };

    public enum Health { Ok, Warning, Critical }

    /// <summary>One honest headline: operational only with a recent full backup on Drive and no blocker.</summary>
    public static (Health Health, string Title, string Detail) Headline(BackupOverviewDto x)
    {
        if (x.Blockers.Count > 0) return (Health.Critical, "Sao lưu tự động chưa hoạt động", x.Blockers[0].Message);
        if (x.LastFull is null) return (Health.Warning, "Chưa có bản sao lưu nào", "Đủ điều kiện chạy; bản đầu tiên được tạo theo lịch hoặc khi bạn bấm Sao lưu ngay.");
        if (x.LastFullJob?.Status == BackupJobStatuses.Failed) return (Health.Critical, "Lần sao lưu gần nhất thất bại", BackupLabels.Error(x.LastFullJob.ErrorCode));
        if (x.Warnings.FirstOrDefault(w => w.Code is "stale" or "paused" or "damaged" or "cv-files" or "log-failed") is { } warning)
            return (Health.Warning, "Cần chú ý", warning.Message);
        if (x.LastRestoreDrill?.Status != BackupJobStatuses.Succeeded)
            return (Health.Warning, "Đã sao lưu, chưa kiểm chứng khôi phục", "Bản đầy đủ mới nhất " + Time(x.LastFull.CreatedAtUtc) + ".");
        return x.LastFull.VerifyResult == "success"
            ? (Health.Ok, "Đang được bảo vệ", "Bản đầy đủ mới nhất " + Time(x.LastFull.CreatedAtUtc) + " trên Google Drive: đã xác minh; đã có lần khôi phục thử thành công.")
            : (Health.Warning, "Đã khôi phục thử, bản mới nhất chưa xác minh", "Bản đầy đủ mới nhất " + Time(x.LastFull.CreatedAtUtc) + ".");
    }

    /// <summary>One of the separate facts an operator must not confuse. State: ok, warning, critical or none.</summary>
    public sealed record Step(string Code, string Label, string State, string Detail);

    public static IReadOnlyList<Step> Steps(BackupOverviewDto x)
    {
        var backup = x.BackupWorker; var restore = x.RestoreWorker;
        Step drive = !x.Drive.Connected ? new("drive", "Google Drive", "critical", "Chưa kết nối")
            : x.Drive.LastCheckOk == false ? new("drive", "Google Drive", "critical", BackupLabels.Error(x.Drive.LastCheckError))
            : new("drive", "Google Drive", "ok", "Đã kết nối · kiểm tra " + Time(x.Drive.LastCheckedAtUtc));
        var running = (backup.Alive, restore.Alive) switch
        {
            (true, true) => ("ok", "Cả hai đang chạy"),
            (true, false) => ("warning", "Worker khôi phục không chạy"),
            (false, true) => ("critical", "Worker sao lưu không chạy"),
            _ => ("critical", backup.Installed || restore.Installed ? "Cả hai không phản hồi" : "Chưa cài đặt")
        };
        var ready = (backup.Alive && backup.Ready, restore.Alive && restore.Ready) switch
        {
            (true, true) => ("ok", "Đủ khóa, quyền và cấu hình"),
            (true, false) => ("warning", "Khôi phục: " + (restore.Alive ? BackupLabels.Error(restore.Problem) : "không chạy")),
            _ => ("critical", "Sao lưu: " + (backup.Alive ? BackupLabels.Error(backup.Problem) : "không chạy"))
        };
        Step made = x.LastFull is not { } full ? new("backup", "Sao lưu thành công", "none", "Chưa có bản nào")
            : new("backup", "Sao lưu thành công", x.LastFullJob?.Status == BackupJobStatuses.Failed ? "warning" : "ok",
                Time(full.CreatedAtUtc) + (x.LastFullJob?.Status == BackupJobStatuses.Failed ? " · lần mới nhất thất bại" : ""));
        var verified = x.LastFull?.VerifyResult switch
        {
            "success" => ("ok", "Bản mới nhất đã giải mã và đối chiếu " + Time(x.LastFull.VerifiedAtUtc)),
            null => ("none", "Bản mới nhất chưa được xác minh"),
            var code => ("critical", BackupLabels.Error(code))
        };
        var drill = x.LastRestoreDrill?.Status switch
        {
            BackupJobStatuses.Succeeded => ("ok", Time(x.LastRestoreDrill.FinishedAtUtc)),
            BackupJobStatuses.Failed => ("critical", BackupLabels.Error(x.LastRestoreDrill.ErrorCode)),
            _ => ("none", "Chưa thực hiện")
        };
        return
        [
            drive,
            new("running", "Worker đang chạy", running.Item1, running.Item2),
            new("ready", "Worker sẵn sàng", ready.Item1, ready.Item2),
            made,
            new("verified", "Đã xác minh toàn vẹn", verified.Item1, verified.Item2),
            new("drill", "Khôi phục thử", drill.Item1, drill.Item2)
        ];
    }

    public static string Tone(Health health) => health switch { Health.Ok => "ok", Health.Warning => "warning", _ => "critical" };
    public static string JobTone(string status) => status switch
    { BackupJobStatuses.Succeeded => "ok", BackupJobStatuses.Failed => "critical", BackupJobStatuses.Missed => "warning", _ => "neutral" };
}
