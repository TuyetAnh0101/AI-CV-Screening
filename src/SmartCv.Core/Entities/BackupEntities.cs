namespace SmartCv.Core.Entities;

/// <summary>A62: one requested or scheduled unit of backup work. The table is the durable job queue.</summary>
public class BackupJob
{
    public Guid BackupJobId { get; set; }
    public string Type { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string Status { get; set; } = "Queued";
    public DateTime RequestedAtUtc { get; set; }
    public int? RequestedByUserId { get; set; }
    /// <summary>Retries wait until this time.</summary>
    public DateTime? NotBeforeUtc { get; set; }
    /// <summary>Scheduled slot (UTC) this job serves; unique per type so restarts never schedule twice.</summary>
    public DateTime? ScheduledSlotUtc { get; set; }
    public int Attempt { get; set; } = 1;
    public Guid? RetryOfJobId { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? HeartbeatAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public string? Stage { get; set; }
    public byte Progress { get; set; }
    public string? ErrorCode { get; set; }
    /// <summary>Point produced (full/log) or restored (drill/recovery).</summary>
    public Guid? BackupPointId { get; set; }
    /// <summary>Point-in-time target for a recovery, in SQL Server local time.</summary>
    public DateTime? StopAtServerTime { get; set; }
    public string? TargetDatabase { get; set; }
    public long? DatabaseBytes { get; set; }
    public long? UploadedBytes { get; set; }
    public int? CvFiles { get; set; }
    public int? NewCvFiles { get; set; }
    public long? NewCvBytes { get; set; }
    public string? WorkerMachine { get; set; }
}

/// <summary>A62: a restore point stored on Google Drive. The encrypted manifest on Drive is the source of truth.</summary>
public class BackupPoint
{
    public Guid BackupPointId { get; set; }
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "Available";
    public string DatabaseName { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public Guid? BackupJobId { get; set; }
    public decimal? FirstLsn { get; set; }
    public decimal? LastLsn { get; set; }
    public DateTime? SqlFinishedServerTime { get; set; }
    public string KeyId { get; set; } = "";
    public string ManifestFileId { get; set; } = "";
    public long ManifestCipherBytes { get; set; }
    public string PayloadFileId { get; set; } = "";
    public long PayloadPlainBytes { get; set; }
    public long PayloadCipherBytes { get; set; }
    public string PayloadCipherSha256 { get; set; } = "";
    public string? KeysFileId { get; set; }
    public long? KeysCipherBytes { get; set; }
    public int CvFiles { get; set; }
    public long CvBytes { get; set; }
    public int KnownMissingCvs { get; set; }
    public string Consistency { get; set; } = "";
    public string? SchemaVersion { get; set; }
    public DateTime? VerifiedAtUtc { get; set; }
    public string? VerifyResult { get; set; }
    public DateTime? RestoreTestedAtUtc { get; set; }
    public string? RestoreTestResult { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
}

/// <summary>A62: one encrypted CV content object on Drive, shared by every point that contains it.</summary>
public class BackupObject
{
    /// <summary>HMAC of the plaintext SHA-256 under the backup key: never reveals the CV hash itself.</summary>
    public string ObjectKey { get; set; } = "";
    public string FileId { get; set; } = "";
    public long PlainBytes { get; set; }
    public long CipherBytes { get; set; }
    public string CipherSha256 { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
}

public class BackupPointObject
{
    public Guid BackupPointId { get; set; }
    public string ObjectKey { get; set; } = "";
}

/// <summary>A62: the single schedule/retention row (Id = 1). Admins change it, workers obey it.</summary>
public class BackupSettings
{
    public byte BackupSettingsId { get; set; } = 1;
    public string FullTimeLocal { get; set; } = "01:30";
    public int LogIntervalHours { get; set; } = 4;
    public int RetentionCount { get; set; } = 14;
    public bool Paused { get; set; }
    public bool WeeklyRestoreDrill { get; set; } = true;
    public DateTime? LastHandledFullSlotUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public int? UpdatedByUserId { get; set; }
}

/// <summary>A62: the single Google Drive connection (Id = 1). Credentials are Data Protection ciphertext.</summary>
public class BackupDriveConnection
{
    public byte BackupDriveConnectionId { get; set; } = 1;
    public string ProtectedCredential { get; set; } = "";
    public string? AccountEmail { get; set; }
    public string FolderId { get; set; } = "";
    public string FolderName { get; set; } = "";
    public DateTime ConnectedAtUtc { get; set; }
    public int? ConnectedByUserId { get; set; }
    public DateTime? LastCheckedAtUtc { get; set; }
    public bool? LastCheckOk { get; set; }
    public string? LastCheckError { get; set; }
    public long? QuotaUsedBytes { get; set; }
    public long? QuotaLimitBytes { get; set; }
}

/// <summary>A62: liveness and readiness that each worker role publishes.</summary>
public class BackupWorkerHeartbeat
{
    public string Role { get; set; } = "";
    public DateTime HeartbeatAtUtc { get; set; }
    public string MachineName { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Ready { get; set; }
    public string? Problem { get; set; }
    public string? KeyId { get; set; }
    public DateTime? KeyEscrowConfirmedAtUtc { get; set; }
    public long? StagingFreeBytes { get; set; }
}
