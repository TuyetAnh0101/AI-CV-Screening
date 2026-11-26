BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126023357_A62GoogleDriveBackup'
)
BEGIN
    CREATE TABLE [BackupDriveConnections] (
        [BackupDriveConnectionId] tinyint NOT NULL,
        [ProtectedCredential] nvarchar(max) NOT NULL,
        [AccountEmail] nvarchar(320) NULL,
        [FolderId] varchar(128) NOT NULL,
        [FolderName] nvarchar(200) NOT NULL,
        [ConnectedAtUtc] datetime2 NOT NULL,
        [ConnectedByUserId] int NULL,
        [LastCheckedAtUtc] datetime2 NULL,
        [LastCheckOk] bit NULL,
        [LastCheckError] varchar(44) NULL,
        [QuotaUsedBytes] bigint NULL,
        [QuotaLimitBytes] bigint NULL,
        CONSTRAINT [PK_BackupDriveConnections] PRIMARY KEY ([BackupDriveConnectionId]),
        CONSTRAINT [CK_BackupDriveConnections_Single] CHECK ([BackupDriveConnectionId] = 1)
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126023357_A62GoogleDriveBackup'
)
BEGIN
    CREATE TABLE [BackupJobs] (
        [BackupJobId] uniqueidentifier NOT NULL,
        [Type] varchar(20) NOT NULL,
        [Trigger] varchar(12) NOT NULL,
        [Status] varchar(12) NOT NULL,
        [RequestedAtUtc] datetime2 NOT NULL,
        [RequestedByUserId] int NULL,
        [NotBeforeUtc] datetime2 NULL,
        [ScheduledSlotUtc] datetime2 NULL,
        [Attempt] int NOT NULL,
        [RetryOfJobId] uniqueidentifier NULL,
        [StartedAtUtc] datetime2 NULL,
        [HeartbeatAtUtc] datetime2 NULL,
        [FinishedAtUtc] datetime2 NULL,
        [Stage] varchar(60) NULL,
        [Progress] tinyint NOT NULL,
        [ErrorCode] varchar(44) NULL,
        [BackupPointId] uniqueidentifier NULL,
        [StopAtServerTime] datetime2 NULL,
        [TargetDatabase] varchar(128) NULL,
        [DatabaseBytes] bigint NULL,
        [UploadedBytes] bigint NULL,
        [CvFiles] int NULL,
        [NewCvFiles] int NULL,
        [NewCvBytes] bigint NULL,
        [WorkerMachine] nvarchar(64) NULL,
        CONSTRAINT [PK_BackupJobs] PRIMARY KEY ([BackupJobId]),
        CONSTRAINT [CK_BackupJobs_Error] CHECK ([ErrorCode] IS NULL OR [ErrorCode] LIKE 'A62[_]%'),
        CONSTRAINT [CK_BackupJobs_Progress] CHECK ([Progress] BETWEEN 0 AND 100),
        CONSTRAINT [CK_BackupJobs_Status] CHECK ([Status] IN ('Queued','Running','Succeeded','Failed','Missed','Cancelled')),
        CONSTRAINT [CK_BackupJobs_Trigger] CHECK ([Trigger] IN ('scheduled','catch-up','retry','manual')),
        CONSTRAINT [CK_BackupJobs_Type] CHECK ([Type] IN ('full','log','verify','restore-drill','recovery','catalog-sync'))
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126023357_A62GoogleDriveBackup'
)
BEGIN
    CREATE TABLE [BackupObjects] (
        [ObjectKey] varchar(64) NOT NULL,
        [FileId] varchar(128) NOT NULL,
        [PlainBytes] bigint NOT NULL,
        [CipherBytes] bigint NOT NULL,
        [CipherSha256] varchar(64) NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [DeletedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_BackupObjects] PRIMARY KEY ([ObjectKey])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126023357_A62GoogleDriveBackup'
)
BEGIN
    CREATE TABLE [BackupPoints] (
        [BackupPointId] uniqueidentifier NOT NULL,
        [Kind] varchar(8) NOT NULL,
        [Status] varchar(12) NOT NULL,
        [DatabaseName] nvarchar(128) NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [BackupJobId] uniqueidentifier NULL,
        [FirstLsn] decimal(25,0) NULL,
        [LastLsn] decimal(25,0) NULL,
        [SqlFinishedServerTime] datetime2 NULL,
        [KeyId] varchar(32) NOT NULL,
        [ManifestFileId] varchar(128) NOT NULL,
        [ManifestCipherBytes] bigint NOT NULL,
        [PayloadFileId] varchar(128) NOT NULL,
        [PayloadPlainBytes] bigint NOT NULL,
        [PayloadCipherBytes] bigint NOT NULL,
        [PayloadCipherSha256] varchar(64) NOT NULL,
        [KeysFileId] varchar(128) NULL,
        [KeysCipherBytes] bigint NULL,
        [CvFiles] int NOT NULL,
        [CvBytes] bigint NOT NULL,
        [KnownMissingCvs] int NOT NULL,
        [Consistency] varchar(32) NOT NULL,
        [SchemaVersion] nvarchar(150) NULL,
        [VerifiedAtUtc] datetime2 NULL,
        [VerifyResult] varchar(44) NULL,
        [RestoreTestedAtUtc] datetime2 NULL,
        [RestoreTestResult] varchar(44) NULL,
        [DeletedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_BackupPoints] PRIMARY KEY ([BackupPointId]),
        CONSTRAINT [CK_BackupPoints_Kind] CHECK ([Kind] IN ('Full','Log')),
        CONSTRAINT [CK_BackupPoints_Status] CHECK ([Status] IN ('Available','Deleted','Damaged'))
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126023357_A62GoogleDriveBackup'
)
BEGIN
    CREATE TABLE [BackupSettings] (
        [BackupSettingsId] tinyint NOT NULL,
        [FullTimeLocal] varchar(5) NOT NULL,
        [LogIntervalHours] int NOT NULL,
        [RetentionCount] int NOT NULL,
        [Paused] bit NOT NULL,
        [WeeklyRestoreDrill] bit NOT NULL,
        [LastHandledFullSlotUtc] datetime2 NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        [UpdatedByUserId] int NULL,
        CONSTRAINT [PK_BackupSettings] PRIMARY KEY ([BackupSettingsId]),
        CONSTRAINT [CK_BackupSettings_Limits] CHECK ([LogIntervalHours] BETWEEN 1 AND 24 AND [RetentionCount] BETWEEN 7 AND 90 AND [FullTimeLocal] LIKE '[0-2][0-9]:[0-5][0-9]'),
        CONSTRAINT [CK_BackupSettings_Single] CHECK ([BackupSettingsId] = 1)
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126023357_A62GoogleDriveBackup'
)
BEGIN
    CREATE TABLE [BackupWorkerHeartbeats] (
        [Role] varchar(10) NOT NULL,
        [HeartbeatAtUtc] datetime2 NOT NULL,
        [MachineName] nvarchar(64) NOT NULL,
        [Version] varchar(40) NOT NULL,
        [Ready] bit NOT NULL,
        [Problem] varchar(44) NULL,
        [KeyId] varchar(32) NULL,
        [KeyEscrowConfirmedAtUtc] datetime2 NULL,
        [StagingFreeBytes] bigint NULL,
        CONSTRAINT [PK_BackupWorkerHeartbeats] PRIMARY KEY ([Role]),
        CONSTRAINT [CK_BackupWorkerHeartbeats_Role] CHECK ([Role] IN ('backup','restore'))
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126023357_A62GoogleDriveBackup'
)
BEGIN
    CREATE TABLE [BackupPointObjects] (
        [BackupPointId] uniqueidentifier NOT NULL,
        [ObjectKey] varchar(64) NOT NULL,
        CONSTRAINT [PK_BackupPointObjects] PRIMARY KEY ([BackupPointId], [ObjectKey]),
        CONSTRAINT [FK_BackupPointObjects_BackupObjects_ObjectKey] FOREIGN KEY ([ObjectKey]) REFERENCES [BackupObjects] ([ObjectKey]) ON DELETE NO ACTION,
        CONSTRAINT [FK_BackupPointObjects_BackupPoints_BackupPointId] FOREIGN KEY ([BackupPointId]) REFERENCES [BackupPoints] ([BackupPointId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126023357_A62GoogleDriveBackup'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'BackupSettingsId', N'FullTimeLocal', N'LastHandledFullSlotUtc', N'LogIntervalHours', N'Paused', N'RetentionCount', N'UpdatedAtUtc', N'UpdatedByUserId', N'WeeklyRestoreDrill') AND [object_id] = OBJECT_ID(N'[BackupSettings]'))
        SET IDENTITY_INSERT [BackupSettings] ON;
    EXEC(N'INSERT INTO [BackupSettings] ([BackupSettingsId], [FullTimeLocal], [LastHandledFullSlotUtc], [LogIntervalHours], [Paused], [RetentionCount], [UpdatedAtUtc], [UpdatedByUserId], [WeeklyRestoreDrill])
    VALUES (CAST(1 AS tinyint), ''01:30'', NULL, 4, CAST(0 AS bit), 14, ''2026-11-26T00:00:00.0000000Z'', NULL, CAST(1 AS bit))');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'BackupSettingsId', N'FullTimeLocal', N'LastHandledFullSlotUtc', N'LogIntervalHours', N'Paused', N'RetentionCount', N'UpdatedAtUtc', N'UpdatedByUserId', N'WeeklyRestoreDrill') AND [object_id] = OBJECT_ID(N'[BackupSettings]'))
        SET IDENTITY_INSERT [BackupSettings] OFF;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126023357_A62GoogleDriveBackup'
)
BEGIN
    CREATE INDEX [IX_BackupJobs_RequestedAtUtc] ON [BackupJobs] ([RequestedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126023357_A62GoogleDriveBackup'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_BackupJobs_ActiveType] ON [BackupJobs] ([Type]) WHERE [Status] IN (''Queued'',''Running'')');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126023357_A62GoogleDriveBackup'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_BackupJobs_Slot] ON [BackupJobs] ([Type], [ScheduledSlotUtc]) WHERE [ScheduledSlotUtc] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126023357_A62GoogleDriveBackup'
)
BEGIN
    CREATE INDEX [IX_BackupPointObjects_ObjectKey] ON [BackupPointObjects] ([ObjectKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126023357_A62GoogleDriveBackup'
)
BEGIN
    CREATE INDEX [IX_BackupPoints_Kind_Status] ON [BackupPoints] ([Kind], [Status], [CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126023357_A62GoogleDriveBackup'
)
BEGIN
    CREATE UNIQUE INDEX [UX_BackupPoints_Manifest] ON [BackupPoints] ([ManifestFileId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261126023357_A62GoogleDriveBackup'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261126023357_A62GoogleDriveBackup', N'10.0.15');
END;

COMMIT;
GO

