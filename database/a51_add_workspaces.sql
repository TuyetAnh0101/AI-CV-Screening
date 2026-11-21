BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Applications] DROP CONSTRAINT [FK_Applications_Cvs];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Applications] DROP CONSTRAINT [FK_Applications_Jobs];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Cvs] DROP CONSTRAINT [FK_Cvs_Candidates];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [EvaluationRuns] DROP CONSTRAINT [FK_EvaluationRuns_EvaluationSets];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [EvaluationRuns] DROP CONSTRAINT [FK_EvaluationRuns_PromptVersions];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [EvaluationSets] DROP CONSTRAINT [FK_EvaluationSets_Jobs];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [PromptVersions] DROP CONSTRAINT [UQ_PromptVersions_VersionName];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    DROP INDEX [UX_PromptVersions_OneActive] ON [PromptVersions];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    DROP INDEX [UQ_Jobs_Title] ON [Jobs];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [EvaluationSets] DROP CONSTRAINT [UQ_EvaluationSets_Name];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Cvs] DROP CONSTRAINT [UQ_Cvs_FileHash];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    CREATE TABLE [Workspaces] (
        [WorkspaceId] int NOT NULL IDENTITY,
        [Name] nvarchar(100) NOT NULL,
        [Kind] varchar(20) NOT NULL,
        [OwnerUserId] int NULL,
        [IsActive] bit NOT NULL CONSTRAINT [DF_Workspaces_IsActive] DEFAULT CAST(1 AS bit),
        [DailyAiEvaluationLimit] int NULL,
        [MaxCvCount] int NULL,
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_Workspaces_CreatedAt] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_Workspaces] PRIMARY KEY ([WorkspaceId]),
        CONSTRAINT [CK_Workspaces_DailyAiEvaluationLimit] CHECK ([DailyAiEvaluationLimit] IS NULL OR [DailyAiEvaluationLimit] >= 0),
        CONSTRAINT [CK_Workspaces_Kind] CHECK ([Kind] COLLATE Latin1_General_100_BIN2 IN ('Internal', 'Personal')),
        CONSTRAINT [CK_Workspaces_MaxCvCount] CHECK ([MaxCvCount] IS NULL OR [MaxCvCount] >= 0),
        CONSTRAINT [FK_Workspaces_Users_Owner] FOREIGN KEY ([OwnerUserId]) REFERENCES [Users] ([UserId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    CREATE TABLE [WorkspaceMembers] (
        [WorkspaceId] int NOT NULL,
        [UserId] int NOT NULL,
        [Role] varchar(10) NOT NULL,
        [JoinedAt] datetime2 NOT NULL CONSTRAINT [DF_WorkspaceMembers_JoinedAt] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_WorkspaceMembers] PRIMARY KEY ([WorkspaceId], [UserId]),
        CONSTRAINT [CK_WorkspaceMembers_Role] CHECK ([Role] COLLATE Latin1_General_100_BIN2 IN ('Owner', 'Member')),
        CONSTRAINT [FK_WorkspaceMembers_Users] FOREIGN KEY ([UserId]) REFERENCES [Users] ([UserId]),
        CONSTRAINT [FK_WorkspaceMembers_Workspaces] FOREIGN KEY ([WorkspaceId]) REFERENCES [Workspaces] ([WorkspaceId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    CREATE TABLE [UserTokens] (
        [UserTokenId] bigint NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [Purpose] varchar(30) NOT NULL,
        [TokenHash] char(64) NOT NULL,
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_UserTokens_CreatedAt] DEFAULT (SYSUTCDATETIME()),
        [ExpiresAt] datetime2 NOT NULL,
        [ConsumedAt] datetime2 NULL,
        [Payload] nvarchar(500) NULL,
        CONSTRAINT [PK_UserTokens] PRIMARY KEY ([UserTokenId]),
        CONSTRAINT [CK_UserTokens_TokenHash] CHECK (LEN([TokenHash]) = 64),
        CONSTRAINT [FK_UserTokens_Users] FOREIGN KEY ([UserId]) REFERENCES [Users] ([UserId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    CREATE TABLE [UserExternalLogins] (
        [UserExternalLoginId] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [Provider] varchar(20) NOT NULL,
        [ProviderKey] nvarchar(200) NOT NULL,
        [EmailAtLink] nvarchar(254) NULL,
        [LinkedAt] datetime2 NOT NULL CONSTRAINT [DF_UserExternalLogins_LinkedAt] DEFAULT (SYSUTCDATETIME()),
        [LastUsedAt] datetime2 NULL,
        CONSTRAINT [PK_UserExternalLogins] PRIMARY KEY ([UserExternalLoginId]),
        CONSTRAINT [FK_UserExternalLogins_Users] FOREIGN KEY ([UserId]) REFERENCES [Users] ([UserId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'WorkspaceId', N'CreatedAt', N'DailyAiEvaluationLimit', N'IsActive', N'Kind', N'MaxCvCount', N'Name', N'OwnerUserId') AND [object_id] = OBJECT_ID(N'[Workspaces]'))
        SET IDENTITY_INSERT [Workspaces] ON;
    EXEC(N'INSERT INTO [Workspaces] ([WorkspaceId], [CreatedAt], [DailyAiEvaluationLimit], [IsActive], [Kind], [MaxCvCount], [Name], [OwnerUserId])
    VALUES (1, ''2026-11-01T00:00:00.0000000Z'', NULL, CAST(1 AS bit), ''Internal'', NULL, N''SCApp nội bộ'', NULL)');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'WorkspaceId', N'CreatedAt', N'DailyAiEvaluationLimit', N'IsActive', N'Kind', N'MaxCvCount', N'Name', N'OwnerUserId') AND [object_id] = OBJECT_ID(N'[Workspaces]'))
        SET IDENTITY_INSERT [Workspaces] OFF;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Users] ADD [EmailVerifiedAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Users] ADD [SignupSource] varchar(20) NOT NULL CONSTRAINT [DF_Users_SignupSource] DEFAULT 'Admin';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Users] ADD [TermsAcceptedAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    EXEC(N'UPDATE [Users] SET [EmailVerifiedAt] = [CreatedAt] WHERE [EmailVerifiedAt] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    EXEC(N'ALTER TABLE [Users] ADD CONSTRAINT [CK_Users_SignupSource] CHECK ([SignupSource] COLLATE Latin1_General_100_BIN2 IN (N''Admin'', N''Local'', N''google'', N''linkedin''))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    EXEC(N'
    INSERT INTO [WorkspaceMembers] ([WorkspaceId], [UserId], [Role], [JoinedAt])
    SELECT 1, u.[UserId],
           CASE WHEN u.[UserId] = (SELECT MIN([UserId]) FROM [Users] WHERE [Role] = N''Admin'') THEN ''Owner'' ELSE ''Member'' END,
           SYSUTCDATETIME()
    FROM [Users] u
    WHERE NOT EXISTS (SELECT 1 FROM [WorkspaceMembers] m WHERE m.[WorkspaceId] = 1 AND m.[UserId] = u.[UserId]);

    UPDATE [Workspaces] SET [OwnerUserId] = (SELECT MIN([UserId]) FROM [Users] WHERE [Role] = N''Admin'') WHERE [WorkspaceId] = 1;');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Jobs] ADD [WorkspaceId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    EXEC(N'UPDATE [Jobs] SET [WorkspaceId] = 1 WHERE [WorkspaceId] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    DECLARE @var nvarchar(max);
    SELECT @var = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Jobs]') AND [c].[name] = N'WorkspaceId');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [Jobs] DROP CONSTRAINT ' + @var + ';');
    ALTER TABLE [Jobs] ALTER COLUMN [WorkspaceId] int NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Cvs] ADD [WorkspaceId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    EXEC(N'UPDATE [Cvs] SET [WorkspaceId] = 1 WHERE [WorkspaceId] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    DECLARE @var1 nvarchar(max);
    SELECT @var1 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Cvs]') AND [c].[name] = N'WorkspaceId');
    IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [Cvs] DROP CONSTRAINT ' + @var1 + ';');
    ALTER TABLE [Cvs] ALTER COLUMN [WorkspaceId] int NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Candidates] ADD [WorkspaceId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    EXEC(N'UPDATE [Candidates] SET [WorkspaceId] = 1 WHERE [WorkspaceId] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    DECLARE @var2 nvarchar(max);
    SELECT @var2 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Candidates]') AND [c].[name] = N'WorkspaceId');
    IF @var2 IS NOT NULL EXEC(N'ALTER TABLE [Candidates] DROP CONSTRAINT ' + @var2 + ';');
    ALTER TABLE [Candidates] ALTER COLUMN [WorkspaceId] int NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Applications] ADD [WorkspaceId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    EXEC(N'UPDATE [Applications] SET [WorkspaceId] = 1 WHERE [WorkspaceId] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    DECLARE @var3 nvarchar(max);
    SELECT @var3 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Applications]') AND [c].[name] = N'WorkspaceId');
    IF @var3 IS NOT NULL EXEC(N'ALTER TABLE [Applications] DROP CONSTRAINT ' + @var3 + ';');
    ALTER TABLE [Applications] ALTER COLUMN [WorkspaceId] int NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [PromptVersions] ADD [WorkspaceId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    EXEC(N'UPDATE [PromptVersions] SET [WorkspaceId] = 1 WHERE [WorkspaceId] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    DECLARE @var4 nvarchar(max);
    SELECT @var4 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[PromptVersions]') AND [c].[name] = N'WorkspaceId');
    IF @var4 IS NOT NULL EXEC(N'ALTER TABLE [PromptVersions] DROP CONSTRAINT ' + @var4 + ';');
    ALTER TABLE [PromptVersions] ALTER COLUMN [WorkspaceId] int NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [EvaluationSets] ADD [WorkspaceId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    EXEC(N'UPDATE [EvaluationSets] SET [WorkspaceId] = 1 WHERE [WorkspaceId] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    DECLARE @var5 nvarchar(max);
    SELECT @var5 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[EvaluationSets]') AND [c].[name] = N'WorkspaceId');
    IF @var5 IS NOT NULL EXEC(N'ALTER TABLE [EvaluationSets] DROP CONSTRAINT ' + @var5 + ';');
    ALTER TABLE [EvaluationSets] ALTER COLUMN [WorkspaceId] int NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [EvaluationRuns] ADD [WorkspaceId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    EXEC(N'UPDATE [EvaluationRuns] SET [WorkspaceId] = 1 WHERE [WorkspaceId] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    DECLARE @var6 nvarchar(max);
    SELECT @var6 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[EvaluationRuns]') AND [c].[name] = N'WorkspaceId');
    IF @var6 IS NOT NULL EXEC(N'ALTER TABLE [EvaluationRuns] DROP CONSTRAINT ' + @var6 + ';');
    ALTER TABLE [EvaluationRuns] ALTER COLUMN [WorkspaceId] int NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [ExportLogs] ADD [WorkspaceId] int NOT NULL CONSTRAINT [DF_ExportLogs_WorkspaceId_Tmp] DEFAULT 1;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    EXEC(N'ALTER TABLE [ExportLogs] DROP CONSTRAINT [DF_ExportLogs_WorkspaceId_Tmp]');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [AuditEvents] ADD [WorkspaceId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [PromptVersions] ADD CONSTRAINT [AK_PromptVersions_Workspace_Prompt] UNIQUE ([WorkspaceId], [PromptVersionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [PromptVersions] ADD CONSTRAINT [UQ_PromptVersions_Workspace_VersionName] UNIQUE ([WorkspaceId], [VersionName]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Jobs] ADD CONSTRAINT [AK_Jobs_Workspace_Job] UNIQUE ([WorkspaceId], [JobId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [EvaluationSets] ADD CONSTRAINT [AK_EvaluationSets_Workspace_Set] UNIQUE ([WorkspaceId], [EvaluationSetId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [EvaluationSets] ADD CONSTRAINT [UQ_EvaluationSets_Workspace_Name] UNIQUE ([WorkspaceId], [Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Cvs] ADD CONSTRAINT [AK_Cvs_Workspace_Cv] UNIQUE ([WorkspaceId], [CvId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Candidates] ADD CONSTRAINT [AK_Candidates_Workspace_Candidate] UNIQUE ([WorkspaceId], [CandidateId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_PromptVersions_Workspace_OneActive] ON [PromptVersions] ([WorkspaceId]) WHERE [IsActive] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    CREATE UNIQUE INDEX [UQ_Jobs_Workspace_Title] ON [Jobs] ([WorkspaceId], [Title]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    CREATE INDEX [IX_ExportLogs_Workspace_Export] ON [ExportLogs] ([WorkspaceId], [ExportId] DESC);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    CREATE INDEX [IX_EvaluationSets_WorkspaceId_JobId] ON [EvaluationSets] ([WorkspaceId], [JobId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    CREATE INDEX [IX_EvaluationRuns_WorkspaceId_EvaluationSetId] ON [EvaluationRuns] ([WorkspaceId], [EvaluationSetId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    CREATE INDEX [IX_EvaluationRuns_WorkspaceId_PromptVersionId] ON [EvaluationRuns] ([WorkspaceId], [PromptVersionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Cvs_Workspace_FileHash] ON [Cvs] ([WorkspaceId], [FileHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    CREATE INDEX [IX_Cvs_WorkspaceId_CandidateId] ON [Cvs] ([WorkspaceId], [CandidateId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    CREATE INDEX [IX_AuditEvents_Workspace_Time] ON [AuditEvents] ([WorkspaceId], [OccurredAtUtc] DESC);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    CREATE INDEX [IX_Applications_WorkspaceId_CvId] ON [Applications] ([WorkspaceId], [CvId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    CREATE INDEX [IX_Applications_WorkspaceId_JobId] ON [Applications] ([WorkspaceId], [JobId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    CREATE UNIQUE INDEX [UX_UserExternalLogins_Provider_Key] ON [UserExternalLogins] ([Provider], [ProviderKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    CREATE UNIQUE INDEX [UX_UserExternalLogins_User_Provider] ON [UserExternalLogins] ([UserId], [Provider]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    CREATE INDEX [IX_UserTokens_User_Purpose_CreatedAt] ON [UserTokens] ([UserId], [Purpose], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    CREATE UNIQUE INDEX [UX_UserTokens_TokenHash] ON [UserTokens] ([TokenHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    CREATE INDEX [IX_WorkspaceMembers_UserId] ON [WorkspaceMembers] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_WorkspaceMembers_OneOwner] ON [WorkspaceMembers] ([WorkspaceId]) WHERE [Role] = ''Owner''');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_Workspaces_PersonalOwner] ON [Workspaces] ([OwnerUserId]) WHERE [Kind] = ''Personal''');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Applications] ADD CONSTRAINT [FK_Applications_Cvs] FOREIGN KEY ([WorkspaceId], [CvId]) REFERENCES [Cvs] ([WorkspaceId], [CvId]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Applications] ADD CONSTRAINT [FK_Applications_Jobs] FOREIGN KEY ([WorkspaceId], [JobId]) REFERENCES [Jobs] ([WorkspaceId], [JobId]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Cvs] ADD CONSTRAINT [FK_Cvs_Candidates] FOREIGN KEY ([WorkspaceId], [CandidateId]) REFERENCES [Candidates] ([WorkspaceId], [CandidateId]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [EvaluationRuns] ADD CONSTRAINT [FK_EvaluationRuns_EvaluationSets] FOREIGN KEY ([WorkspaceId], [EvaluationSetId]) REFERENCES [EvaluationSets] ([WorkspaceId], [EvaluationSetId]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [EvaluationRuns] ADD CONSTRAINT [FK_EvaluationRuns_PromptVersions] FOREIGN KEY ([WorkspaceId], [PromptVersionId]) REFERENCES [PromptVersions] ([WorkspaceId], [PromptVersionId]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [EvaluationSets] ADD CONSTRAINT [FK_EvaluationSets_Jobs] FOREIGN KEY ([WorkspaceId], [JobId]) REFERENCES [Jobs] ([WorkspaceId], [JobId]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Applications] ADD CONSTRAINT [FK_Applications_Workspaces] FOREIGN KEY ([WorkspaceId]) REFERENCES [Workspaces] ([WorkspaceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [AuditEvents] ADD CONSTRAINT [FK_AuditEvents_Workspaces] FOREIGN KEY ([WorkspaceId]) REFERENCES [Workspaces] ([WorkspaceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Candidates] ADD CONSTRAINT [FK_Candidates_Workspaces] FOREIGN KEY ([WorkspaceId]) REFERENCES [Workspaces] ([WorkspaceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Cvs] ADD CONSTRAINT [FK_Cvs_Workspaces] FOREIGN KEY ([WorkspaceId]) REFERENCES [Workspaces] ([WorkspaceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [EvaluationRuns] ADD CONSTRAINT [FK_EvaluationRuns_Workspaces] FOREIGN KEY ([WorkspaceId]) REFERENCES [Workspaces] ([WorkspaceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [EvaluationSets] ADD CONSTRAINT [FK_EvaluationSets_Workspaces] FOREIGN KEY ([WorkspaceId]) REFERENCES [Workspaces] ([WorkspaceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [ExportLogs] ADD CONSTRAINT [FK_ExportLogs_Workspaces] FOREIGN KEY ([WorkspaceId]) REFERENCES [Workspaces] ([WorkspaceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [Jobs] ADD CONSTRAINT [FK_Jobs_Workspaces] FOREIGN KEY ([WorkspaceId]) REFERENCES [Workspaces] ([WorkspaceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    ALTER TABLE [PromptVersions] ADD CONSTRAINT [FK_PromptVersions_Workspaces] FOREIGN KEY ([WorkspaceId]) REFERENCES [Workspaces] ([WorkspaceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261121040902_AddWorkspaces'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261121040902_AddWorkspaces', N'10.0.15');
END;

COMMIT;
GO
