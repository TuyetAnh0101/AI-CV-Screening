using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

public sealed class BackupJobConfiguration : IEntityTypeConfiguration<BackupJob>
{
    public void Configure(EntityTypeBuilder<BackupJob> b)
    {
        b.ToTable("BackupJobs", t =>
        {
            t.HasCheckConstraint("CK_BackupJobs_Type", "[Type] IN ('full','log','verify','restore-drill','recovery','catalog-sync')");
            t.HasCheckConstraint("CK_BackupJobs_Trigger", "[Trigger] IN ('scheduled','catch-up','retry','manual')");
            t.HasCheckConstraint("CK_BackupJobs_Status", "[Status] IN ('Queued','Running','Succeeded','Failed','Missed','Cancelled')");
            t.HasCheckConstraint("CK_BackupJobs_Progress", "[Progress] BETWEEN 0 AND 100");
            t.HasCheckConstraint("CK_BackupJobs_Error", "[ErrorCode] IS NULL OR [ErrorCode] LIKE 'A62[_]%'");
        });
        b.HasKey(x => x.BackupJobId);
        b.Property(x => x.BackupJobId).ValueGeneratedNever();
        b.Property(x => x.Type).HasMaxLength(20).IsUnicode(false).IsRequired();
        b.Property(x => x.Trigger).HasMaxLength(12).IsUnicode(false).IsRequired();
        b.Property(x => x.Status).HasMaxLength(12).IsUnicode(false).IsRequired();
        b.Property(x => x.Stage).HasMaxLength(60).IsUnicode(false);
        b.Property(x => x.ErrorCode).HasMaxLength(44).IsUnicode(false);
        b.Property(x => x.TargetDatabase).HasMaxLength(128).IsUnicode(false);
        b.Property(x => x.WorkerMachine).HasMaxLength(64);
        b.Property(x => x.Progress).HasColumnType("tinyint");
        foreach (var name in new[] { nameof(BackupJob.RequestedAtUtc), nameof(BackupJob.NotBeforeUtc), nameof(BackupJob.ScheduledSlotUtc),
            nameof(BackupJob.StartedAtUtc), nameof(BackupJob.HeartbeatAtUtc), nameof(BackupJob.FinishedAtUtc), nameof(BackupJob.StopAtServerTime) })
            b.Property(name).HasColumnType("datetime2");
        // One queued/running job per type: the database, not the caller, prevents duplicate work.
        b.HasIndex(x => x.Type).IsUnique().HasFilter("[Status] IN ('Queued','Running')").HasDatabaseName("UX_BackupJobs_ActiveType");
        // A scheduled slot is scheduled (or recorded as missed) exactly once, across restarts.
        b.HasIndex(x => new { x.Type, x.ScheduledSlotUtc }).IsUnique().HasFilter("[ScheduledSlotUtc] IS NOT NULL").HasDatabaseName("UX_BackupJobs_Slot");
        b.HasIndex(x => x.RequestedAtUtc).HasDatabaseName("IX_BackupJobs_RequestedAtUtc");
    }
}

public sealed class BackupPointConfiguration : IEntityTypeConfiguration<BackupPoint>
{
    public void Configure(EntityTypeBuilder<BackupPoint> b)
    {
        b.ToTable("BackupPoints", t =>
        {
            t.HasCheckConstraint("CK_BackupPoints_Kind", "[Kind] IN ('Full','Log')");
            t.HasCheckConstraint("CK_BackupPoints_Status", "[Status] IN ('Available','Deleted','Damaged')");
        });
        b.HasKey(x => x.BackupPointId);
        b.Property(x => x.BackupPointId).ValueGeneratedNever();
        b.Property(x => x.Kind).HasMaxLength(8).IsUnicode(false).IsRequired();
        b.Property(x => x.Status).HasMaxLength(12).IsUnicode(false).IsRequired();
        b.Property(x => x.DatabaseName).HasMaxLength(128).IsRequired();
        b.Property(x => x.FirstLsn).HasColumnType("decimal(25,0)");
        b.Property(x => x.LastLsn).HasColumnType("decimal(25,0)");
        b.Property(x => x.KeyId).HasMaxLength(32).IsUnicode(false).IsRequired();
        foreach (var name in new[] { nameof(BackupPoint.ManifestFileId), nameof(BackupPoint.PayloadFileId), nameof(BackupPoint.KeysFileId) })
            b.Property(name).HasMaxLength(128).IsUnicode(false);
        b.Property(x => x.PayloadCipherSha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        b.Property(x => x.Consistency).HasMaxLength(32).IsUnicode(false).IsRequired();
        b.Property(x => x.SchemaVersion).HasMaxLength(150);
        b.Property(x => x.VerifyResult).HasMaxLength(44).IsUnicode(false);
        b.Property(x => x.RestoreTestResult).HasMaxLength(44).IsUnicode(false);
        foreach (var name in new[] { nameof(BackupPoint.CreatedAtUtc), nameof(BackupPoint.SqlFinishedServerTime), nameof(BackupPoint.VerifiedAtUtc),
            nameof(BackupPoint.RestoreTestedAtUtc), nameof(BackupPoint.DeletedAtUtc) })
            b.Property(name).HasColumnType("datetime2");
        b.HasIndex(x => x.ManifestFileId).IsUnique().HasDatabaseName("UX_BackupPoints_Manifest");
        b.HasIndex(x => new { x.Kind, x.Status, x.CreatedAtUtc }).HasDatabaseName("IX_BackupPoints_Kind_Status");
    }
}

public sealed class BackupObjectConfiguration : IEntityTypeConfiguration<BackupObject>
{
    public void Configure(EntityTypeBuilder<BackupObject> b)
    {
        b.ToTable("BackupObjects");
        b.HasKey(x => x.ObjectKey);
        b.Property(x => x.ObjectKey).HasMaxLength(64).IsUnicode(false);
        b.Property(x => x.FileId).HasMaxLength(128).IsUnicode(false).IsRequired();
        b.Property(x => x.CipherSha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2");
        b.Property(x => x.DeletedAtUtc).HasColumnType("datetime2");
    }
}

public sealed class BackupPointObjectConfiguration : IEntityTypeConfiguration<BackupPointObject>
{
    public void Configure(EntityTypeBuilder<BackupPointObject> b)
    {
        b.ToTable("BackupPointObjects");
        b.HasKey(x => new { x.BackupPointId, x.ObjectKey });
        b.Property(x => x.ObjectKey).HasMaxLength(64).IsUnicode(false);
        b.HasOne<BackupPoint>().WithMany().HasForeignKey(x => x.BackupPointId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BackupObject>().WithMany().HasForeignKey(x => x.ObjectKey).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ObjectKey).HasDatabaseName("IX_BackupPointObjects_ObjectKey");
    }
}

public sealed class BackupSettingsConfiguration : IEntityTypeConfiguration<BackupSettings>
{
    public void Configure(EntityTypeBuilder<BackupSettings> b)
    {
        b.ToTable("BackupSettings", t =>
        {
            t.HasCheckConstraint("CK_BackupSettings_Single", "[BackupSettingsId] = 1");
            t.HasCheckConstraint("CK_BackupSettings_Limits", "[LogIntervalHours] BETWEEN 1 AND 24 AND [RetentionCount] BETWEEN 7 AND 90 AND [FullTimeLocal] LIKE '[0-2][0-9]:[0-5][0-9]'");
        });
        b.HasKey(x => x.BackupSettingsId);
        b.Property(x => x.BackupSettingsId).HasColumnType("tinyint").ValueGeneratedNever();
        b.Property(x => x.FullTimeLocal).HasMaxLength(5).IsUnicode(false).IsRequired();
        b.Property(x => x.LastHandledFullSlotUtc).HasColumnType("datetime2");
        b.Property(x => x.UpdatedAtUtc).HasColumnType("datetime2");
        b.HasData(new BackupSettings { BackupSettingsId = 1, UpdatedAtUtc = new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc) });
    }
}

public sealed class BackupDriveConnectionConfiguration : IEntityTypeConfiguration<BackupDriveConnection>
{
    public void Configure(EntityTypeBuilder<BackupDriveConnection> b)
    {
        b.ToTable("BackupDriveConnections", t => t.HasCheckConstraint("CK_BackupDriveConnections_Single", "[BackupDriveConnectionId] = 1"));
        b.HasKey(x => x.BackupDriveConnectionId);
        b.Property(x => x.BackupDriveConnectionId).HasColumnType("tinyint").ValueGeneratedNever();
        b.Property(x => x.ProtectedCredential).HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.AccountEmail).HasMaxLength(320);
        b.Property(x => x.FolderId).HasMaxLength(128).IsUnicode(false).IsRequired();
        b.Property(x => x.FolderName).HasMaxLength(200).IsRequired();
        b.Property(x => x.LastCheckError).HasMaxLength(44).IsUnicode(false);
        b.Property(x => x.ConnectedAtUtc).HasColumnType("datetime2");
        b.Property(x => x.LastCheckedAtUtc).HasColumnType("datetime2");
    }
}

public sealed class BackupWorkerHeartbeatConfiguration : IEntityTypeConfiguration<BackupWorkerHeartbeat>
{
    public void Configure(EntityTypeBuilder<BackupWorkerHeartbeat> b)
    {
        b.ToTable("BackupWorkerHeartbeats", t => t.HasCheckConstraint("CK_BackupWorkerHeartbeats_Role", "[Role] IN ('backup','restore')"));
        b.HasKey(x => x.Role);
        b.Property(x => x.Role).HasMaxLength(10).IsUnicode(false);
        b.Property(x => x.MachineName).HasMaxLength(64).IsRequired();
        b.Property(x => x.Version).HasMaxLength(40).IsUnicode(false).IsRequired();
        b.Property(x => x.Problem).HasMaxLength(44).IsUnicode(false);
        b.Property(x => x.KeyId).HasMaxLength(32).IsUnicode(false);
        b.Property(x => x.HeartbeatAtUtc).HasColumnType("datetime2");
        b.Property(x => x.KeyEscrowConfirmedAtUtc).HasColumnType("datetime2");
    }
}
