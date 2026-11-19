BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119024613_ScappWorkflowV2'
)
BEGIN
    ALTER TABLE [EvaluationSets] ADD [JobId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119024613_ScappWorkflowV2'
)
BEGIN
    ALTER TABLE [EvaluationSets] ADD [Purpose] nvarchar(300) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119024613_ScappWorkflowV2'
)
BEGIN
    ALTER TABLE [EvaluationSets] ADD [Revision] int NOT NULL CONSTRAINT [DF_EvaluationSets_Revision] DEFAULT 1;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119024613_ScappWorkflowV2'
)
BEGIN
    ALTER TABLE [EvaluationSets] ADD [UpdatedAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119024613_ScappWorkflowV2'
)
BEGIN
    ALTER TABLE [EvaluationRuns] ADD [EvaluationSetRevision] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119024613_ScappWorkflowV2'
)
BEGIN
    CREATE TABLE [ApplicationFlags] (
        [FlagId] int NOT NULL IDENTITY,
        [ApplicationId] int NOT NULL,
        [Reason] nvarchar(30) NOT NULL,
        [Note] nvarchar(500) NULL,
        [Status] nvarchar(20) NOT NULL,
        [CreatedBy] nvarchar(100) NOT NULL,
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_ApplicationFlags_CreatedAt] DEFAULT (SYSUTCDATETIME()),
        [ResolvedBy] nvarchar(100) NULL,
        [ResolvedAt] datetime2 NULL,
        [ResolutionNote] nvarchar(500) NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [Version] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_ApplicationFlags] PRIMARY KEY ([FlagId]),
        CONSTRAINT [CK_ApplicationFlags_CreatedBy] CHECK (LEN(LTRIM(RTRIM([CreatedBy]))) > 0),
        CONSTRAINT [CK_ApplicationFlags_Reason] CHECK ([Reason] COLLATE Latin1_General_100_BIN2 IN (N'ScoreDoubt', N'ExtractionIssue', N'MissingInformation', N'SecondOpinion', N'VerifyRequirement', N'Other')),
        CONSTRAINT [CK_ApplicationFlags_Resolved] CHECK (([Status] = N'Open' AND [ResolvedAt] IS NULL AND [ResolvedBy] IS NULL) OR ([Status] = N'Resolved' AND [ResolvedAt] IS NOT NULL AND [ResolvedBy] IS NOT NULL)),
        CONSTRAINT [CK_ApplicationFlags_Status] CHECK ([Status] COLLATE Latin1_General_100_BIN2 IN (N'Open', N'Resolved')),
        CONSTRAINT [FK_ApplicationFlags_Applications] FOREIGN KEY ([ApplicationId]) REFERENCES [Applications] ([ApplicationId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119024613_ScappWorkflowV2'
)
BEGIN
    CREATE TABLE [EvaluationSetMembers] (
        [EvaluationSetId] int NOT NULL,
        [ApplicationId] int NOT NULL,
        [AddedBy] nvarchar(100) NOT NULL,
        [AddedAt] datetime2 NOT NULL CONSTRAINT [DF_EvaluationSetMembers_AddedAt] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_EvaluationSetMembers] PRIMARY KEY ([EvaluationSetId], [ApplicationId]),
        CONSTRAINT [FK_EvaluationSetMembers_Applications] FOREIGN KEY ([ApplicationId]) REFERENCES [Applications] ([ApplicationId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_EvaluationSetMembers_EvaluationSets] FOREIGN KEY ([EvaluationSetId]) REFERENCES [EvaluationSets] ([EvaluationSetId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119024613_ScappWorkflowV2'
)
BEGIN
    CREATE TABLE [ApplicationFlagEvents] (
        [EventId] bigint NOT NULL IDENTITY,
        [FlagId] int NOT NULL,
        [Action] nvarchar(20) NOT NULL,
        [Actor] nvarchar(100) NOT NULL,
        [Note] nvarchar(500) NULL,
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_ApplicationFlagEvents_CreatedAt] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_ApplicationFlagEvents] PRIMARY KEY ([EventId]),
        CONSTRAINT [CK_ApplicationFlagEvents_Action] CHECK ([Action] COLLATE Latin1_General_100_BIN2 IN (N'Opened', N'Resolved', N'Reopened')),
        CONSTRAINT [CK_ApplicationFlagEvents_Actor] CHECK (LEN(LTRIM(RTRIM([Actor]))) > 0),
        CONSTRAINT [FK_ApplicationFlagEvents_ApplicationFlags] FOREIGN KEY ([FlagId]) REFERENCES [ApplicationFlags] ([FlagId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119024613_ScappWorkflowV2'
)
BEGIN
    CREATE INDEX [IX_EvaluationSets_JobId] ON [EvaluationSets] ([JobId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119024613_ScappWorkflowV2'
)
BEGIN
    EXEC(N'ALTER TABLE [EvaluationSets] ADD CONSTRAINT [CK_EvaluationSets_Revision] CHECK ([Revision] >= 1)');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119024613_ScappWorkflowV2'
)
BEGIN
    CREATE INDEX [IX_ApplicationFlagEvents_Flag_CreatedAt] ON [ApplicationFlagEvents] ([FlagId], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119024613_ScappWorkflowV2'
)
BEGIN
    CREATE INDEX [IX_ApplicationFlags_Application_CreatedAt] ON [ApplicationFlags] ([ApplicationId], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119024613_ScappWorkflowV2'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_ApplicationFlags_OpenPerApplication] ON [ApplicationFlags] ([ApplicationId]) WHERE [Status] = N''Open''');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119024613_ScappWorkflowV2'
)
BEGIN
    CREATE INDEX [IX_EvaluationSetMembers_ApplicationId] ON [EvaluationSetMembers] ([ApplicationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119024613_ScappWorkflowV2'
)
BEGIN
    ALTER TABLE [EvaluationSets] ADD CONSTRAINT [FK_EvaluationSets_Jobs] FOREIGN KEY ([JobId]) REFERENCES [Jobs] ([JobId]) ON DELETE SET NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119024613_ScappWorkflowV2'
)
BEGIN
    INSERT INTO dbo.EvaluationSetMembers (EvaluationSetId, ApplicationId, AddedBy, AddedAt)
    SELECT h.EvaluationSetId, h.ApplicationId, h.LabeledBy, h.LabeledAt
    FROM dbo.HrLabels h
    WHERE NOT EXISTS (SELECT 1 FROM dbo.EvaluationSetMembers m
                      WHERE m.EvaluationSetId = h.EvaluationSetId AND m.ApplicationId = h.ApplicationId);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261119024613_ScappWorkflowV2'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261119024613_ScappWorkflowV2', N'10.0.15');
END;

COMMIT;
GO
