BEGIN TRANSACTION;
ALTER TABLE [Applications] ADD [RejectReason] nvarchar(30) NULL;

ALTER TABLE [Applications] ADD [Stage] nvarchar(20) NOT NULL DEFAULT N'New';

ALTER TABLE [Applications] ADD [StageUpdatedAt] datetime2 NULL;

ALTER TABLE [Applications] ADD [StageVersion] uniqueidentifier NOT NULL DEFAULT (NEWID());

CREATE TABLE [ApplicationEvents] (
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
    CONSTRAINT [CK_ApplicationEvents_FromStage] CHECK ([FromStage] COLLATE Latin1_General_100_BIN2 IN (N'New', N'Reviewing', N'Shortlisted', N'Interview', N'Rejected', N'OnHold')),
    CONSTRAINT [CK_ApplicationEvents_RejectReason] CHECK ([RejectReason] IS NULL OR [RejectReason] COLLATE Latin1_General_100_BIN2 IN (N'MissingSkills', N'NotEnoughExperience', N'OtherRequirement', N'CandidateWithdrew', N'PositionFilled', N'Duplicate')),
    CONSTRAINT [CK_ApplicationEvents_RejectReason_Stage] CHECK (([ToStage] = N'Rejected' AND [RejectReason] IS NOT NULL) OR ([ToStage] <> N'Rejected' AND [RejectReason] IS NULL)),
    CONSTRAINT [CK_ApplicationEvents_ToStage] CHECK ([ToStage] COLLATE Latin1_General_100_BIN2 IN (N'New', N'Reviewing', N'Shortlisted', N'Interview', N'Rejected', N'OnHold')),
    CONSTRAINT [FK_ApplicationEvents_Applications] FOREIGN KEY ([ApplicationId]) REFERENCES [Applications] ([ApplicationId]) ON DELETE NO ACTION
);

CREATE INDEX [IX_Applications_Job_Stage] ON [Applications] ([JobId], [Stage]);

ALTER TABLE [Applications] ADD CONSTRAINT [CK_Applications_RejectReason] CHECK ([RejectReason] IS NULL OR [RejectReason] COLLATE Latin1_General_100_BIN2 IN (N'MissingSkills', N'NotEnoughExperience', N'OtherRequirement', N'CandidateWithdrew', N'PositionFilled', N'Duplicate'));

ALTER TABLE [Applications] ADD CONSTRAINT [CK_Applications_RejectReason_Stage] CHECK (([Stage] = N'Rejected' AND [RejectReason] IS NOT NULL) OR ([Stage] <> N'Rejected' AND [RejectReason] IS NULL));

ALTER TABLE [Applications] ADD CONSTRAINT [CK_Applications_Stage] CHECK ([Stage] COLLATE Latin1_General_100_BIN2 IN (N'New', N'Reviewing', N'Shortlisted', N'Interview', N'Rejected', N'OnHold'));

CREATE INDEX [IX_ApplicationEvents_Application_CreatedAt] ON [ApplicationEvents] ([ApplicationId], [CreatedAt] DESC);

EXEC(N'CREATE TRIGGER [dbo].[TR_ApplicationEvents_NoChange]
ON [dbo].[ApplicationEvents]
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, N''Lịch sử quyết định chỉ được thêm, không được sửa hoặc xóa.'', 1;
END');

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261112045258_ApplicationStages', N'10.0.15');

COMMIT;
GO