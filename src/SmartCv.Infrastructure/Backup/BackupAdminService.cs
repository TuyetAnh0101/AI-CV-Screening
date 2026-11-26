using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Backup;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Backup;

/// <summary>What the Admin page reads and requests. Never runs SQL BACKUP/RESTORE or talks to files on the server.</summary>
public sealed class BackupAdminService(SmartCvDbContext db, TimeProvider clock)
{
    /// <summary>A worker that has not reported for this long is treated as stopped.</summary>
    public static readonly TimeSpan WorkerStale = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan FullStale = TimeSpan.FromHours(26);
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<BackupOverviewDto> OverviewAsync(bool driveConfigured, CancellationToken ct)
    {
        var now = Now;
        // The migration seeds the single row; a store without it (not yet saved) reads as the defaults.
        var settings = await db.BackupSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new BackupSettings();
        var heartbeats = await db.BackupWorkerHeartbeats.AsNoTracking().ToListAsync(ct);
        BackupWorkerDto Worker(string role)
        {
            var h = heartbeats.SingleOrDefault(x => x.Role == role);
            return h is null ? new(false, false, false, null, null, null, null, null)
                : new(true, now - h.HeartbeatAtUtc <= WorkerStale, h.Ready, h.Problem, h.HeartbeatAtUtc, h.KeyId, h.KeyEscrowConfirmedAtUtc, h.StagingFreeBytes);
        }
        var backup = Worker(BackupWorkerRoles.Backup); var restore = Worker(BackupWorkerRoles.Restore);
        var c = await db.BackupDriveConnections.AsNoTracking().SingleOrDefaultAsync(ct);
        var checkOk = c?.LastCheckOk; var checkError = c?.LastCheckError; var checkedAt = c?.LastCheckedAtUtc;
        if (c is not null)
        {
            // A job that was refused by Google after the last check outranks that older "healthy" answer.
            var since = c.LastCheckedAtUtc ?? c.ConnectedAtUtc;
            var refused = await db.BackupJobs.AsNoTracking().Where(j => j.ErrorCode == BackupErrors.DriveAuth && j.FinishedAtUtc > since && j.FinishedAtUtc > c.ConnectedAtUtc)
                .OrderByDescending(j => j.FinishedAtUtc).Select(j => j.FinishedAtUtc).FirstOrDefaultAsync(ct);
            if (refused is not null) { checkOk = false; checkError = BackupErrors.DriveAuth; checkedAt = refused; }
        }
        var drive = new BackupDriveDto(driveConfigured, c is not null, c?.AccountEmail, c?.FolderName, c?.ConnectedAtUtc, checkedAt,
            checkOk, checkError, c?.QuotaUsedBytes, c?.QuotaLimitBytes);
        var lastFull = await db.BackupPoints.AsNoTracking().Where(p => p.Kind == BackupPointKinds.Full && p.Status == BackupPointStatuses.Available)
            .OrderByDescending(p => p.CreatedAtUtc).FirstOrDefaultAsync(ct);
        var lastLog = await db.BackupPoints.AsNoTracking().Where(p => p.Kind == BackupPointKinds.Log && p.Status == BackupPointStatuses.Available)
            .OrderByDescending(p => p.CreatedAtUtc).FirstOrDefaultAsync(ct);
        async Task<BackupJobDto?> Latest(string type) => await db.BackupJobs.AsNoTracking().Where(j => j.Type == type && j.Status != BackupJobStatuses.Queued && j.Status != BackupJobStatuses.Running)
            .OrderByDescending(j => j.RequestedAtUtc).Select(Dto).FirstOrDefaultAsync(ct);
        var lastFullJob = await Latest(BackupJobTypes.Full);
        var active = await db.BackupJobs.AsNoTracking().Where(j => j.Status == BackupJobStatuses.Queued || j.Status == BackupJobStatuses.Running)
            .OrderBy(j => j.RequestedAtUtc).Select(Dto).ToListAsync(ct);
        var points = await db.BackupPoints.AsNoTracking().Where(p => p.Status != BackupPointStatuses.Deleted)
            .Select(p => new { p.Kind, p.Status, Size = p.PayloadCipherBytes + (p.KeysCipherBytes ?? 0) + p.ManifestCipherBytes }).ToListAsync(ct);
        var objectBytes = await db.BackupObjects.AsNoTracking().Where(o => o.DeletedAtUtc == null).SumAsync(o => (long?)o.CipherBytes, ct) ?? 0;

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(now, DateTimeKind.Utc), clock.LocalTimeZone);
        DateTime ToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), clock.LocalTimeZone);
        DateTime? nextFull = settings.Paused ? null : ToUtc(BackupSchedule.NextFull(settings.FullTimeLocal, localNow));
        var retry = active.FirstOrDefault(j => j.Type == BackupJobTypes.Full && j.Status == BackupJobStatuses.Queued && j.NotBeforeUtc is not null);
        if (retry?.NotBeforeUtc is { } at && (nextFull is null || at < nextFull)) nextFull = at;
        var lastLogRequest = await db.BackupJobs.AsNoTracking().Where(j => j.Type == BackupJobTypes.Log).OrderByDescending(j => j.RequestedAtUtc).Select(j => (DateTime?)j.RequestedAtUtc).FirstOrDefaultAsync(ct);
        DateTime? nextLog = settings.Paused || lastFull?.LastLsn is null ? null : (lastLogRequest ?? now).AddHours(settings.LogIntervalHours);

        var drill = await Latest(BackupJobTypes.RestoreDrill);
        var blockers = new List<BackupNotice>();
        var warnings = new List<BackupNotice>();
        if (!driveConfigured) blockers.Add(new("drive-not-configured", "Máy chủ chưa có OAuth Client cho Google Drive.", "Cấu hình Client ID/Secret trên máy chủ (xem mục Google Drive)."));
        else if (c is null) blockers.Add(new("drive-not-connected", "Chưa kết nối Google Drive.", "Mở tab Google Drive và bấm Kết nối."));
        else if (checkOk == false) blockers.Add(new("drive-failed", BackupLabels.Error(checkError), "Mở tab Google Drive để kiểm tra hoặc kết nối lại."));
        if (!backup.Installed) blockers.Add(new("worker-missing", "Worker sao lưu chưa được cài trên máy chủ.", "Người vận hành cài dịch vụ SCApp Backup Worker."));
        else if (!backup.Alive) blockers.Add(new("worker-stopped", "Worker sao lưu không phản hồi.", "Kiểm tra dịch vụ SCApp Backup Worker và máy chủ."));
        else if (!backup.Ready && backup.Problem != BackupErrors.DriveNotConnected) blockers.Add(new("worker-problem", BackupLabels.Error(backup.Problem), "Chạy lệnh check của worker trên máy chủ."));
        if (settings.Paused) warnings.Add(new("paused", "Lịch tự động đang tạm dừng.", "Bật lại trong tab Lịch."));
        if (backup.Installed && backup.KeyId is not null && backup.KeyEscrowConfirmedAtUtc is null)
            warnings.Add(new("key-escrow", "Chưa xác nhận đã cất bản sao khóa mã hóa ngoài máy chủ.", "Mất máy chủ mà không có khóa thì không giải mã được backup."));
        if (lastFull is null) warnings.Add(new("no-backup", "Chưa có bản sao lưu nào trên Google Drive.", "Bấm Sao lưu ngay sau khi hết các điều kiện chặn."));
        else if (!settings.Paused && now - lastFull.CreatedAtUtc > FullStale) warnings.Add(new("stale", "Bản sao lưu mới nhất đã quá 26 giờ.", "Kiểm tra lịch và lỗi gần nhất trong Báo cáo."));
        if (lastFullJob is { Status: BackupJobStatuses.Failed }) warnings.Add(new("full-failed", "Lần sao lưu gần nhất thất bại: " + BackupLabels.Error(lastFullJob.ErrorCode),
            BackupErrors.IsTransient(lastFullJob.ErrorCode) ? "Bấm Thử lại trong tab Báo cáo, hoặc chờ lần thử tự động." : BackupLabels.Guidance(lastFullJob.ErrorCode)));
        var latestLogJob = await Latest(BackupJobTypes.Log);
        if (latestLogJob is { Status: BackupJobStatuses.Failed }) warnings.Add(new("log-failed", "Sao lưu transaction log gần nhất thất bại: " + BackupLabels.Error(latestLogJob.ErrorCode),
            BackupErrors.IsTransient(latestLogJob.ErrorCode) ? "Lần chạy theo lịch kế tiếp sẽ thử lại." : BackupLabels.Guidance(latestLogJob.ErrorCode)));
        if (!restore.Installed || !restore.Alive) warnings.Add(new("restore-worker", "Worker khôi phục chưa hoạt động: không thể khôi phục thử.", "Người vận hành cài/khởi động SCApp Restore Worker."));
        else if (!restore.Ready && restore.Problem != BackupErrors.DriveNotConnected)
            warnings.Add(new("restore-not-ready", "Worker khôi phục chưa sẵn sàng: " + BackupLabels.Error(restore.Problem), BackupLabels.Guidance(restore.Problem)));
        if (backup is { Alive: true, Ready: true, Problem: BackupErrors.Files })
            warnings.Add(new("cv-files", "Có tệp CV mà cơ sở dữ liệu tham chiếu đang thiếu hoặc sai: bản sao lưu đầy đủ sẽ thất bại.", BackupLabels.Guidance(BackupErrors.Files)));
        if (backup is { Alive: true, Ready: true, Problem: BackupErrors.ExcessPrivilege })
            warnings.Add(new("privilege", "Tài khoản worker sao lưu đang có quyền sysadmin hoặc tạo database.", "Thu hồi quyền thừa của tài khoản dịch vụ."));
        if (lastFull is not null && lastFull.VerifyResult != "success")
            warnings.Add(new("not-verified", "Bản đầy đủ mới nhất chưa được xác minh toàn vẹn.", "Chạy Kiểm tra toàn vẹn (tự động hằng tuần)."));
        if (lastFull is not null && drill is not { Status: BackupJobStatuses.Succeeded }) warnings.Add(new("no-drill", "Chưa có lần khôi phục thử thành công.", "Chạy Khôi phục thử."));
        if (points.Any(p => p.Status == BackupPointStatuses.Damaged)) warnings.Add(new("damaged", "Có điểm khôi phục bị hỏng hoặc thiếu tệp trên Drive.", "Xem tab Bản sao lưu và tạo bản đầy đủ mới."));
        if (lastFull is { KnownMissingCvs: > 0 }) warnings.Add(new("known-missing", $"{lastFull.KnownMissingCvs} hồ sơ không có tệp CV gốc (đã được ghi nhận): bản sao không chứa các tệp này.", "Chỉ ghi nhận tệp thật sự không tồn tại."));

        return new BackupOverviewDto
        {
            CapturedAtUtc = now, Settings = Settings(settings), BackupWorker = backup, RestoreWorker = restore, Drive = drive,
            LastFull = lastFull is null ? null : Point(lastFull), LastLog = lastLog is null ? null : Point(lastLog), LastFullJob = lastFullJob, Active = active,
            NextFullAtUtc = blockers.Count == 0 ? nextFull : null, NextLogAtUtc = blockers.Count == 0 ? nextLog : null,
            RestorePoints = points.Count(p => p.Kind == BackupPointKinds.Full && p.Status == BackupPointStatuses.Available),
            StoredBytes = points.Sum(p => p.Size) + objectBytes, LastVerify = await Latest(BackupJobTypes.Verify), LastRestoreDrill = drill,
            Blockers = blockers, Warnings = warnings
        };
    }

    public async Task<List<BackupPointDto>> PointsAsync(CancellationToken ct) =>
        (await db.BackupPoints.AsNoTracking().Where(p => p.Status != BackupPointStatuses.Deleted).OrderByDescending(p => p.CreatedAtUtc).Take(500).ToListAsync(ct))
        .Select(Point).ToList();

    public async Task<List<BackupJobDto>> JobsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        // Widened by a day each side: the report filters by Vietnam calendar days.
        var start = from.ToDateTime(TimeOnly.MinValue).AddDays(-1); var end = to.ToDateTime(TimeOnly.MinValue).AddDays(2);
        return await db.BackupJobs.AsNoTracking().Where(j => j.RequestedAtUtc >= start && j.RequestedAtUtc < end)
            .OrderByDescending(j => j.RequestedAtUtc).Take(5000).Select(Dto).ToListAsync(ct);
    }

    /// <summary>Queues a job. The database's unique index allows one queued/running job per type.</summary>
    public async Task<(BackupRequestOutcome Outcome, Guid? Id, string? Detail)> RequestAsync(string type, int userId, bool driveConfigured,
        CancellationToken ct, Guid? pointId = null, DateTime? stopAtServerTime = null)
    {
        if (!BackupJobTypes.All.Contains(type)) return (BackupRequestOutcome.Invalid, null, "Loại thao tác không hợp lệ.");
        var overview = await OverviewAsync(driveConfigured, ct);
        var needsRestore = BackupJobTypes.RestoreRole.Contains(type);
        var worker = needsRestore ? overview.RestoreWorker : overview.BackupWorker;
        if (!overview.Drive.Connected) return (BackupRequestOutcome.Blocked, null, "Chưa kết nối Google Drive.");
        if (!worker.Installed || !worker.Alive) return (BackupRequestOutcome.Blocked, null, needsRestore ? "Worker khôi phục chưa hoạt động." : "Worker sao lưu chưa hoạt động.");
        if (!worker.Ready) return (BackupRequestOutcome.Blocked, null, BackupLabels.Error(worker.Problem));
        if (overview.Drive is { LastCheckOk: false, LastCheckError: BackupErrors.DriveAuth })
            return (BackupRequestOutcome.Blocked, null, BackupLabels.Error(BackupErrors.DriveAuth));
        // Jobs that need an existing point are refused here instead of failing later with a misleading reason.
        var missing = type switch
        {
            BackupJobTypes.Log => !await db.BackupPoints.AnyAsync(p => p.Kind == BackupPointKinds.Full && p.Status == BackupPointStatuses.Available && p.LastLsn != null, ct),
            BackupJobTypes.RestoreDrill or BackupJobTypes.Recovery when pointId is null =>
                !await db.BackupPoints.AnyAsync(p => p.Kind == BackupPointKinds.Full && p.Status == BackupPointStatuses.Available, ct),
            BackupJobTypes.Verify => !await db.BackupPoints.AnyAsync(p => p.Status != BackupPointStatuses.Deleted, ct),
            _ => false
        };
        if (missing) return (BackupRequestOutcome.Blocked, null, BackupLabels.Error(BackupErrors.NoBackup));
        if (pointId is { } id)
        {
            var point = await db.BackupPoints.AsNoTracking().SingleOrDefaultAsync(p => p.BackupPointId == id, ct);
            if (point is null || point.Kind != BackupPointKinds.Full || point.Status != BackupPointStatuses.Available)
                return (BackupRequestOutcome.NotFound, null, "Điểm khôi phục không tồn tại hoặc không dùng được.");
            if (stopAtServerTime is { } stop && (point.SqlFinishedServerTime is null || stop < point.SqlFinishedServerTime))
                return (BackupRequestOutcome.Invalid, null, "Thời điểm khôi phục phải sau bản đầy đủ đã chọn.");
        }
        var active = await db.BackupJobs.AsNoTracking().Where(j => j.Type == type && (j.Status == BackupJobStatuses.Queued || j.Status == BackupJobStatuses.Running))
            .Select(j => (Guid?)j.BackupJobId).FirstOrDefaultAsync(ct);
        if (active is not null) return (BackupRequestOutcome.AlreadyPending, active, "Đã có yêu cầu cùng loại đang chờ hoặc đang chạy.");
        var job = new BackupJob
        {
            BackupJobId = Guid.NewGuid(), Type = type, Trigger = BackupTriggers.Manual, Status = BackupJobStatuses.Queued, RequestedAtUtc = Now,
            RequestedByUserId = userId, BackupPointId = pointId, StopAtServerTime = stopAtServerTime
        };
        db.BackupJobs.Add(job);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return (BackupRequestOutcome.AlreadyPending, null, "Đã có yêu cầu cùng loại đang chờ hoặc đang chạy."); }
        return (BackupRequestOutcome.Accepted, job.BackupJobId, null);
    }

    public async Task<(bool Ok, string? Detail)> UpdateSettingsAsync(BackupSettingsDto change, int userId, CancellationToken ct)
    {
        if (!change.IsValid()) return (false, "Giờ chạy HH:mm, chu kỳ log 1–24 giờ, số điểm khôi phục 7–90.");
        var settings = await db.BackupSettings.SingleOrDefaultAsync(ct);
        if (settings is null) db.BackupSettings.Add(settings = new BackupSettings());
        if (settings.FullTimeLocal != change.FullTime || (settings.Paused && !change.Paused))
        {
            // A new time or resume never creates a backdated or "missed" slot.
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(Now, DateTimeKind.Utc), clock.LocalTimeZone);
            settings.LastHandledFullSlotUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(BackupSchedule.SlotAtOrBefore(local, change.FullTime), DateTimeKind.Unspecified), clock.LocalTimeZone);
        }
        settings.FullTimeLocal = change.FullTime; settings.LogIntervalHours = change.LogIntervalHours; settings.RetentionCount = change.RetentionCount;
        settings.Paused = change.Paused; settings.WeeklyRestoreDrill = change.WeeklyRestoreDrill; settings.UpdatedAtUtc = Now; settings.UpdatedByUserId = userId;
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    private static BackupSettingsDto Settings(BackupSettings s) => new(s.FullTimeLocal, s.LogIntervalHours, s.RetentionCount, s.Paused, s.WeeklyRestoreDrill, s.UpdatedAtUtc);

    private static BackupPointDto Point(BackupPoint p) => new(p.BackupPointId, p.Kind, p.Status, p.CreatedAtUtc, p.SqlFinishedServerTime,
        p.PayloadPlainBytes, p.CvBytes, p.CvFiles, p.KnownMissingCvs, p.KeyId, p.Consistency, p.VerifiedAtUtc, p.VerifyResult, p.RestoreTestedAtUtc, p.RestoreTestResult);

    private static readonly System.Linq.Expressions.Expression<Func<BackupJob, BackupJobDto>> Dto = j => new BackupJobDto(j.BackupJobId, j.Type, j.Trigger,
        j.Status, j.RequestedAtUtc, j.NotBeforeUtc, j.StartedAtUtc, j.FinishedAtUtc, j.Stage, j.Progress, j.ErrorCode, j.BackupPointId, j.TargetDatabase,
        j.DatabaseBytes, j.UploadedBytes, j.CvFiles, j.NewCvFiles, j.NewCvBytes, j.Attempt);
}
