-- A31: ExportLogs, incremental from B43. Review before manual application.
-- Does not change users, scores, CVs, jobs or existing audit/history rows.
-- Set @ExpectedDatabase explicitly. Run with sqlcmd -I -b -f 65001 -d <approved-catalog> -i <this-file>.
-- Requires migration 20261120043759_AddAuditEvents. Never executed automatically.
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @ExpectedDatabase sysname = N'REVIEW_DATABASE_NAME_BEFORE_EXECUTION';
IF DB_NAME() <> @ExpectedDatabase
    THROW 51032, N'A31: sai database. Kiem tra va sua @ExpectedDatabase truoc khi chay.', 1;
IF SESSIONPROPERTY('QUOTED_IDENTIFIER') <> 1
    THROW 51032, N'A31: can QUOTED_IDENTIFIER ON; dung sqlcmd -I -b.', 1;
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL OR OBJECT_ID(N'dbo.AuditEvents', N'U') IS NULL
    THROW 51032, N'A31: thieu schema AuditEvents/B43. Dung lai va ap dung migration da duoc phe duyet.', 1;
IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20261120043759_AddAuditEvents')
    THROW 51032, N'A31: chua co migration 20261120043759_AddAuditEvents.', 1;
IF OBJECT_ID(N'dbo.ExportLogs', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20261120114152_AddRankingExportLogs')
    THROW 51032, N'A31: da co ExportLogs nhung thieu lich su migration. Dung lai de kiem tra thu cong.', 1;

-- EF generated, idempotent (begin)
BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120114152_AddRankingExportLogs'
)
BEGIN
    CREATE TABLE [ExportLogs] (
        [ExportId] bigint NOT NULL IDENTITY,
        [AuditEventId] bigint NOT NULL,
        [RunId] int NOT NULL,
        [JobId] int NOT NULL,
        [Format] varchar(8) NOT NULL,
        [Stage] varchar(20) NOT NULL,
        [Fit] varchar(20) NOT NULL,
        [SearchApplied] bit NOT NULL,
        [NeedsReview] bit NOT NULL,
        [ManualFlags] bit NOT NULL,
        [RowCount] int NOT NULL,
        [Anonymized] bit NOT NULL,
        CONSTRAINT [PK_ExportLogs] PRIMARY KEY ([ExportId]),
        CONSTRAINT [CK_ExportLogs_Fit] CHECK ([Fit] COLLATE Latin1_General_100_BIN2 IN ('all','pass','notpass','unscored','excellent','good','potential','poor','issue')),
        CONSTRAINT [CK_ExportLogs_Format] CHECK ([Format] COLLATE Latin1_General_100_BIN2 = 'xlsx'),
        CONSTRAINT [CK_ExportLogs_Rows] CHECK ([RowCount] > 0 AND [RowCount] <= 5000),
        CONSTRAINT [CK_ExportLogs_Stage] CHECK ([Stage] COLLATE Latin1_General_100_BIN2 IN ('all','New','Reviewing','Shortlisted','Interview','Rejected','OnHold')),
        CONSTRAINT [FK_ExportLogs_AuditEvents_AuditEventId] FOREIGN KEY ([AuditEventId]) REFERENCES [AuditEvents] ([AuditEventId]),
        CONSTRAINT [FK_ExportLogs_EvaluationRuns_RunId] FOREIGN KEY ([RunId]) REFERENCES [EvaluationRuns] ([RunId]),
        CONSTRAINT [FK_ExportLogs_Jobs_JobId] FOREIGN KEY ([JobId]) REFERENCES [Jobs] ([JobId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120114152_AddRankingExportLogs'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ExportLogs_AuditEventId] ON [ExportLogs] ([AuditEventId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120114152_AddRankingExportLogs'
)
BEGIN
    CREATE INDEX [IX_ExportLogs_JobId_ExportId] ON [ExportLogs] ([JobId], [ExportId] DESC);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120114152_AddRankingExportLogs'
)
BEGIN
    CREATE INDEX [IX_ExportLogs_RunId_ExportId] ON [ExportLogs] ([RunId], [ExportId] DESC);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120114152_AddRankingExportLogs'
)
BEGIN
    EXEC(N'CREATE TRIGGER [dbo].[TR_ExportLogs_NoChange]
    ON [dbo].[ExportLogs] AFTER UPDATE, DELETE AS
    BEGIN
        SET NOCOUNT ON;
        IF EXISTS (SELECT 1 FROM deleted)
            THROW 51031, N''Export logs are append-only.'', 1;
    END');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120114152_AddRankingExportLogs'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261120114152_AddRankingExportLogs', N'10.0.15');
END;

COMMIT;
GO


-- EF generated (end)
