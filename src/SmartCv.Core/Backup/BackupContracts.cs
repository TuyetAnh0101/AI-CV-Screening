namespace SmartCv.Core.Backup;

public sealed record BackupSettingsDto(string FullTime, int LogIntervalHours, int RetentionCount, bool Paused, bool WeeklyRestoreDrill,
    DateTime? UpdatedAtUtc)
{
    public bool IsValid() => BackupLimits.IsValidTime(FullTime)
        && LogIntervalHours is >= BackupLimits.MinLogHours and <= BackupLimits.MaxLogHours
        && RetentionCount is >= BackupLimits.MinRetention and <= BackupLimits.MaxRetention;
}

public sealed record BackupWorkerDto(bool Installed, bool Alive, bool Ready, string? Problem, DateTime? HeartbeatAtUtc,
    string? KeyId, DateTime? KeyEscrowConfirmedAtUtc, long? StagingFreeBytes);

public sealed record BackupDriveDto(bool Configured, bool Connected, string? AccountEmail, string? FolderName, DateTime? ConnectedAtUtc,
    DateTime? LastCheckedAtUtc, bool? LastCheckOk, string? LastCheckError, long? QuotaUsedBytes, long? QuotaLimitBytes);

public sealed record BackupJobDto(Guid Id, string Type, string Trigger, string Status, DateTime RequestedAtUtc, DateTime? NotBeforeUtc,
    DateTime? StartedAtUtc, DateTime? FinishedAtUtc, string? Stage, int Progress, string? ErrorCode, Guid? PointId, string? TargetDatabase,
    long? DatabaseBytes, long? UploadedBytes, int? CvFiles, int? NewCvFiles, long? NewCvBytes, int Attempt);

public sealed record BackupPointDto(Guid Id, string Kind, string Status, DateTime CreatedAtUtc, DateTime? SqlFinishedServerTime,
    long PayloadBytes, long CvBytes, int CvFiles, int KnownMissingCvs, string KeyId, string Consistency,
    DateTime? VerifiedAtUtc, string? VerifyResult, DateTime? RestoreTestedAtUtc, string? RestoreTestResult);

public sealed record BackupNotice(string Code, string Message, string Action);

public sealed record BackupOverviewDto
{
    public DateTime CapturedAtUtc { get; init; }
    public BackupSettingsDto Settings { get; init; } = new("01:30", 4, 14, false, true, null);
    public BackupWorkerDto BackupWorker { get; init; } = new(false, false, false, null, null, null, null, null);
    public BackupWorkerDto RestoreWorker { get; init; } = new(false, false, false, null, null, null, null, null);
    public BackupDriveDto Drive { get; init; } = new(false, false, null, null, null, null, null, null, null, null);
    public BackupPointDto? LastFull { get; init; }
    public BackupPointDto? LastLog { get; init; }
    public BackupJobDto? LastFullJob { get; init; }
    public IReadOnlyList<BackupJobDto> Active { get; init; } = [];
    public DateTime? NextFullAtUtc { get; init; }
    public DateTime? NextLogAtUtc { get; init; }
    public int RestorePoints { get; init; }
    public long StoredBytes { get; init; }
    public BackupJobDto? LastVerify { get; init; }
    public BackupJobDto? LastRestoreDrill { get; init; }
    /// <summary>Why automatic backups cannot run now; empty when the pipeline is operational.</summary>
    public IReadOnlyList<BackupNotice> Blockers { get; init; } = [];
    public IReadOnlyList<BackupNotice> Warnings { get; init; } = [];
}

public sealed record BackupRecoveryRequest(Guid PointId, DateTime? StopAtServerTime, string Confirmation);
public sealed record BackupJobAccepted(Guid Id, string Type);
public sealed record BackupAck(bool Ok);
public sealed record BackupDriveConnectStart(string AuthorizationUrl);

public sealed record BackupReportFilter(DateOnly From, DateOnly To, string? Status, string? Type, string? Trigger);
public sealed record BackupReportWeek(DateOnly WeekStart, int Succeeded, int Failed, int Missed);

public sealed record BackupReportDto
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public DateTime GeneratedAtUtc { get; init; }
    public int Total { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int Missed { get; init; }
    /// <summary>Null when nothing finished: never an invented 100 %.</summary>
    public double? SuccessRate { get; init; }
    public DateTime? LastSuccessAtUtc { get; init; }
    public DateTime? NextFullAtUtc { get; init; }
    public DateTime? LastVerifyAtUtc { get; init; }
    public DateTime? LastRestoreDrillAtUtc { get; init; }
    public string? LastRestoreDrillStatus { get; init; }
    public long StoredBytes { get; init; }
    public long? LastDatabaseBytes { get; init; }
    public long? LastCvBytes { get; init; }
    public long UploadedBytes { get; init; }
    public long NewCvBytes { get; init; }
    public int RestorePoints { get; init; }
    public IReadOnlyList<BackupNotice> OpenWarnings { get; init; } = [];
    public IReadOnlyList<BackupReportWeek> Weeks { get; init; } = [];
    public IReadOnlyList<BackupJobDto> Jobs { get; init; } = [];
}

public enum BackupRequestOutcome { Accepted, AlreadyPending, Blocked, Invalid, NotFound }

/// <summary>Days are Vietnam calendar days (UTC+7), matching the other Admin reports.</summary>
public static class BackupReportBuilder
{
    private static readonly TimeSpan Zone = TimeSpan.FromHours(7);
    public static DateOnly Day(DateTime utc) => DateOnly.FromDateTime(utc.Add(Zone));
    private static DateOnly Week(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    public static BackupReportDto Build(IReadOnlyList<BackupJobDto> all, BackupOverviewDto overview, BackupReportFilter filter, DateTime nowUtc)
    {
        var jobs = all.Where(j => Day(j.RequestedAtUtc) >= filter.From && Day(j.RequestedAtUtc) <= filter.To)
            .Where(j => filter.Status is null || j.Status == filter.Status)
            .Where(j => filter.Type is null || j.Type == filter.Type)
            .Where(j => filter.Trigger is null || (filter.Trigger == "manual") == (j.Trigger == BackupTriggers.Manual))
            .OrderByDescending(j => j.RequestedAtUtc).ToList();
        int Count(string status) => jobs.Count(j => j.Status == status);
        var finished = jobs.Count(j => j.Status is BackupJobStatuses.Succeeded or BackupJobStatuses.Failed or BackupJobStatuses.Missed);
        var succeeded = Count(BackupJobStatuses.Succeeded);
        return new BackupReportDto
        {
            From = filter.From, To = filter.To, GeneratedAtUtc = nowUtc, Total = jobs.Count, Succeeded = succeeded,
            Failed = Count(BackupJobStatuses.Failed), Missed = Count(BackupJobStatuses.Missed),
            SuccessRate = finished == 0 ? null : Math.Round(succeeded * 100.0 / finished, 1),
            LastSuccessAtUtc = overview.LastFull?.CreatedAtUtc, NextFullAtUtc = overview.NextFullAtUtc,
            LastVerifyAtUtc = overview.LastVerify?.FinishedAtUtc, LastRestoreDrillAtUtc = overview.LastRestoreDrill?.FinishedAtUtc,
            LastRestoreDrillStatus = overview.LastRestoreDrill?.Status, StoredBytes = overview.StoredBytes,
            LastDatabaseBytes = overview.LastFull?.PayloadBytes, LastCvBytes = overview.LastFull?.CvBytes,
            UploadedBytes = jobs.Sum(j => j.UploadedBytes ?? 0), NewCvBytes = jobs.Sum(j => j.NewCvBytes ?? 0),
            RestorePoints = overview.RestorePoints, OpenWarnings = [.. overview.Blockers, .. overview.Warnings],
            Weeks = jobs.Where(j => j.Status is BackupJobStatuses.Succeeded or BackupJobStatuses.Failed or BackupJobStatuses.Missed)
                .GroupBy(j => Week(Day(j.RequestedAtUtc))).OrderBy(g => g.Key)
                .Select(g => new BackupReportWeek(g.Key, g.Count(j => j.Status == BackupJobStatuses.Succeeded),
                    g.Count(j => j.Status == BackupJobStatuses.Failed), g.Count(j => j.Status == BackupJobStatuses.Missed))).ToList(),
            Jobs = jobs
        };
    }
}
