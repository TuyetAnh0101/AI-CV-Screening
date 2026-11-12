/* REVIEW BEFORE EXECUTION. Applies only ApplicationStages, never creates/drops a database.
   Set @ExpectedDatabase to the database explicitly approved for deployment.
   Stop concurrent schema deployments; arrange backup/maintenance according to your deployment process.
   This script has NOT been applied to the application database by Codex.
   Run sqlcmd with -I (QUOTED_IDENTIFIER ON, as EF Core/SqlClient) and -f 65001 (UTF-8; the trigger message is Vietnamese).
   Without them sqlcmd reads a file saved without BOM using the ANSI code page and creates objects with QUOTED_IDENTIFIER OFF;
   the preflight checks below stop before any change in that case.
     PowerShell: sqlcmd -S 'localhost\BB' -d 'SmartCvDb' -E -b -I -f 65001 -i '<approved copy>.sql'
     CMD:        sqlcmd -S "localhost\BB" -d "SmartCvDb" -E -b -I -f 65001 -i "<approved copy>.sql" */
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @ExpectedDatabase sysname = N'REVIEW_DATABASE_NAME_BEFORE_EXECUTION';
IF @ExpectedDatabase = N'REVIEW_DATABASE_NAME_BEFORE_EXECUTION' OR DB_NAME() <> @ExpectedDatabase
    THROW 51035, N'Database has not been explicitly approved. Set and verify @ExpectedDatabase first.', 1;
-- N'ị' must arrive as the single character U+1ECB (7883); otherwise the file was decoded with the wrong code page.
IF LEN(N'ị') <> 1 OR UNICODE(N'ị') <> 7883
    THROW 51035, N'Script text was decoded with the wrong code page; the trigger message would be corrupted. Run sqlcmd with -f 65001.', 1;
IF SESSIONPROPERTY('QUOTED_IDENTIFIER') <> 1
    THROW 51035, N'QUOTED_IDENTIFIER is OFF (sqlcmd default). Run sqlcmd with -I so objects match the EF Core migration.', 1;
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 51035, N'Missing EF migration history. Do not run this script on an unknown database.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261106142155_SeedJobs')
    THROW 51035, N'SeedJobs must be applied first. Review deployment dependencies.', 1;
BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @LockResult int;
    EXEC @LockResult = sys.sp_getapplock @Resource = N'SmartCv.ApplicationStages.Deployment',
        @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
    IF @LockResult < 0 THROW 51035, N'Another deployment holds the ApplicationStages lock.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261112045258_ApplicationStages')
    BEGIN
        IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Applications')
                   AND name IN (N'Stage', N'RejectReason', N'StageUpdatedAt', N'StageVersion'))
           OR OBJECT_ID(N'dbo.ApplicationEvents', N'U') IS NOT NULL
            THROW 51035, N'Partial or unmanaged A35 schema exists. Stop and investigate; no automatic repair.', 1;
        EXEC(N'ALTER TABLE [Applications] ADD [RejectReason] nvarchar(30) NULL;');

        EXEC(N'ALTER TABLE [Applications] ADD [Stage] nvarchar(20) NOT NULL DEFAULT N''New'';');

        EXEC(N'ALTER TABLE [Applications] ADD [StageUpdatedAt] datetime2 NULL;');

        EXEC(N'ALTER TABLE [Applications] ADD [StageVersion] uniqueidentifier NOT NULL DEFAULT (NEWID());');

        EXEC(N'CREATE TABLE [ApplicationEvents] (
    [EventId] bigint NOT NULL IDENTITY,
    [ApplicationId] int NOT NULL,
    [FromStage] nvarchar(20) NOT NULL,
    [ToStage] nvarchar(20) NOT NULL,
    [RejectReason] nvarchar(30) NULL,
    [Note] nvarchar(500) NULL,
    [Actor] nvarchar(100) NOT NULL,
    [CreatedAt] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT [PK_ApplicationEvents] PRIMARY KEY ([EventId]),
    CONSTRAINT [CK_ApplicationEvents_Actor] CHECK (LEN(LTRIM(RTRIM([Actor]))) > 0),
    CONSTRAINT [CK_ApplicationEvents_FromStage] CHECK ([FromStage] COLLATE Latin1_General_100_BIN2 IN (N''New'', N''Reviewing'', N''Shortlisted'', N''Interview'', N''Rejected'', N''OnHold'')),
    CONSTRAINT [CK_ApplicationEvents_RejectReason] CHECK ([RejectReason] IS NULL OR [RejectReason] COLLATE Latin1_General_100_BIN2 IN (N''MissingSkills'', N''NotEnoughExperience'', N''OtherRequirement'', N''CandidateWithdrew'', N''PositionFilled'', N''Duplicate'')),
    CONSTRAINT [CK_ApplicationEvents_RejectReason_Stage] CHECK (([ToStage] = N''Rejected'' AND [RejectReason] IS NOT NULL) OR ([ToStage] <> N''Rejected'' AND [RejectReason] IS NULL)),
    CONSTRAINT [CK_ApplicationEvents_ToStage] CHECK ([ToStage] COLLATE Latin1_General_100_BIN2 IN (N''New'', N''Reviewing'', N''Shortlisted'', N''Interview'', N''Rejected'', N''OnHold'')),
    CONSTRAINT [FK_ApplicationEvents_Applications] FOREIGN KEY ([ApplicationId]) REFERENCES [Applications] ([ApplicationId]) ON DELETE NO ACTION
);');

        EXEC(N'CREATE INDEX [IX_Applications_Job_Stage] ON [Applications] ([JobId], [Stage]);');

        EXEC(N'ALTER TABLE [Applications] ADD CONSTRAINT [CK_Applications_RejectReason] CHECK ([RejectReason] IS NULL OR [RejectReason] COLLATE Latin1_General_100_BIN2 IN (N''MissingSkills'', N''NotEnoughExperience'', N''OtherRequirement'', N''CandidateWithdrew'', N''PositionFilled'', N''Duplicate''));');

        EXEC(N'ALTER TABLE [Applications] ADD CONSTRAINT [CK_Applications_RejectReason_Stage] CHECK (([Stage] = N''Rejected'' AND [RejectReason] IS NOT NULL) OR ([Stage] <> N''Rejected'' AND [RejectReason] IS NULL));');

        EXEC(N'ALTER TABLE [Applications] ADD CONSTRAINT [CK_Applications_Stage] CHECK ([Stage] COLLATE Latin1_General_100_BIN2 IN (N''New'', N''Reviewing'', N''Shortlisted'', N''Interview'', N''Rejected'', N''OnHold''));');

        EXEC(N'CREATE INDEX [IX_ApplicationEvents_Application_CreatedAt] ON [ApplicationEvents] ([ApplicationId], [CreatedAt] DESC);');

        EXEC(N'EXEC(N''CREATE TRIGGER [dbo].[TR_ApplicationEvents_NoChange]
ON [dbo].[ApplicationEvents]
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, N''''Lịch sử quyết định chỉ được thêm, không được sửa hoặc xóa.'''', 1;
END'');');

        EXEC(N'INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N''20261112045258_ApplicationStages'', N''10.0.15'');');
    END;
    IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Applications')
        AND name IN (N'Stage', N'RejectReason', N'StageUpdatedAt', N'StageVersion')) <> 4
        THROW 51035, N'ApplicationStages columns are incomplete.', 1;
    IF OBJECT_ID(N'dbo.ApplicationEvents', N'U') IS NULL
        THROW 51035, N'ApplicationEvents was not created.', 1;
    IF (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id IN (OBJECT_ID(N'dbo.Applications'), OBJECT_ID(N'dbo.ApplicationEvents'))
        AND name IN (N'CK_Applications_Stage', N'CK_Applications_RejectReason', N'CK_Applications_RejectReason_Stage',
                     N'CK_ApplicationEvents_FromStage', N'CK_ApplicationEvents_ToStage', N'CK_ApplicationEvents_RejectReason',
                     N'CK_ApplicationEvents_RejectReason_Stage', N'CK_ApplicationEvents_Actor') AND is_disabled=0 AND is_not_trusted=0) <> 8
        THROW 51035, N'ApplicationStages CHECK constraints are incomplete or disabled.', 1;
    IF (SELECT COUNT(*) FROM sys.indexes WHERE object_id IN (OBJECT_ID(N'dbo.Applications'), OBJECT_ID(N'dbo.ApplicationEvents'))
        AND name IN (N'IX_Applications_Job_Stage', N'IX_ApplicationEvents_Application_CreatedAt') AND is_disabled=0) <> 2
        THROW 51035, N'ApplicationStages indexes are incomplete or disabled.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name=N'FK_ApplicationEvents_Applications'
        AND parent_object_id=OBJECT_ID(N'dbo.ApplicationEvents') AND referenced_object_id=OBJECT_ID(N'dbo.Applications')
        AND delete_referential_action=0 AND is_disabled=0 AND is_not_trusted=0)
        THROW 51035, N'ApplicationEvents restrictive foreign key is missing.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.triggers WHERE object_id = OBJECT_ID(N'dbo.TR_ApplicationEvents_NoChange')
        AND parent_id=OBJECT_ID(N'dbo.ApplicationEvents') AND is_disabled=0 AND is_instead_of_trigger=1
        AND EXISTS (SELECT 1 FROM sys.trigger_events e WHERE e.object_id=OBJECT_ID(N'dbo.TR_ApplicationEvents_NoChange') AND e.type_desc=N'UPDATE')
        AND EXISTS (SELECT 1 FROM sys.trigger_events e WHERE e.object_id=OBJECT_ID(N'dbo.TR_ApplicationEvents_NoChange') AND e.type_desc=N'DELETE'))
        THROW 51035, N'ApplicationEvents protection trigger is missing or disabled.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.sql_modules WHERE object_id = OBJECT_ID(N'dbo.TR_ApplicationEvents_NoChange')
        AND uses_quoted_identifier = 1 AND uses_ansi_nulls = 1
        AND CHARINDEX(N'Lịch sử quyết định chỉ được thêm, không được sửa hoặc xóa.', definition) > 0)
        THROW 51035, N'ApplicationEvents trigger differs from the EF Core migration (SET options or message). Stop and investigate; no automatic repair.', 1;
    COMMIT TRANSACTION;
    SELECT N'ApplicationStages ready' AS DeploymentStatus;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
