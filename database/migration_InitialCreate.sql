IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
CREATE TABLE [Candidates] (
    [CandidateId] int NOT NULL IDENTITY,
    [FullName] nvarchar(150) NOT NULL,
    [Email] nvarchar(254) NULL,
    [PhoneNumber] char(10) NULL,
    [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_Candidates_CreatedAt] DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT [PK_Candidates] PRIMARY KEY ([CandidateId]),
    CONSTRAINT [CK_Candidates_PhoneNumber] CHECK (PhoneNumber NOT LIKE '%[^0-9]%')
);

CREATE TABLE [EvaluationSets] (
    [EvaluationSetId] int NOT NULL IDENTITY,
    [Name] nvarchar(150) NOT NULL,
    [Description] nvarchar(500) NULL,
    [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_EvaluationSets_CreatedAt] DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT [PK_EvaluationSets] PRIMARY KEY ([EvaluationSetId]),
    CONSTRAINT [UQ_EvaluationSets_Name] UNIQUE ([Name])
);

CREATE TABLE [Jobs] (
    [JobId] int NOT NULL IDENTITY,
    [Title] nvarchar(150) NOT NULL,
    [Description] nvarchar(max) NULL,
    [MinGpa] decimal(3,2) NULL,
    [MinYearsExperience] decimal(4,1) NOT NULL CONSTRAINT [DF_Jobs_MinYearsExperience] DEFAULT 0.0,
    [IsActive] bit NOT NULL CONSTRAINT [DF_Jobs_IsActive] DEFAULT CAST(1 AS bit),
    [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_Jobs_CreatedAt] DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT [PK_Jobs] PRIMARY KEY ([JobId]),
    CONSTRAINT [UQ_Jobs_Title] UNIQUE ([Title]),
    CONSTRAINT [CK_Jobs_MinGpa] CHECK (MinGpa BETWEEN 0 AND 4),
    CONSTRAINT [CK_Jobs_MinYearsExperience] CHECK (MinYearsExperience >= 0)
);

CREATE TABLE [PromptVersions] (
    [PromptVersionId] int NOT NULL IDENTITY,
    [VersionName] nvarchar(100) NOT NULL,
    [SystemPrompt] nvarchar(max) NOT NULL,
    [ChangeNote] nvarchar(500) NULL,
    [IsActive] bit NOT NULL CONSTRAINT [DF_PromptVersions_IsActive] DEFAULT CAST(0 AS bit),
    [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_PromptVersions_CreatedAt] DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT [PK_PromptVersions] PRIMARY KEY ([PromptVersionId]),
    CONSTRAINT [UQ_PromptVersions_VersionName] UNIQUE ([VersionName])
);

CREATE TABLE [Cvs] (
    [CvId] uniqueidentifier NOT NULL,
    [CandidateId] int NULL,
    [OriginalFileName] nvarchar(255) NOT NULL,
    [StoredPath] nvarchar(500) NOT NULL,
    [ContentType] nvarchar(100) NOT NULL,
    [FileSizeBytes] bigint NOT NULL,
    [FileHash] char(64) NOT NULL,
    [ExtractedText] nvarchar(max) NULL,
    [ExtractionMethod] nvarchar(20) NULL,
    [ExtractionStatus] nvarchar(20) NOT NULL CONSTRAINT [DF_Cvs_ExtractionStatus] DEFAULT N'Pending',
    [ExtractionError] nvarchar(1000) NULL,
    [UploadedAt] datetime2 NOT NULL CONSTRAINT [DF_Cvs_UploadedAt] DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT [PK_Cvs] PRIMARY KEY ([CvId]),
    CONSTRAINT [UQ_Cvs_FileHash] UNIQUE ([FileHash]),
    CONSTRAINT [CK_Cvs_ExtractionMethod] CHECK (ExtractionMethod IN (N'PdfText', N'PdfOcr', N'ImageOcr')),
    CONSTRAINT [CK_Cvs_ExtractionStatus] CHECK (ExtractionStatus IN (N'Pending', N'Completed', N'Failed')),
    CONSTRAINT [CK_Cvs_FileSizeBytes] CHECK (FileSizeBytes > 0),
    CONSTRAINT [FK_Cvs_Candidates] FOREIGN KEY ([CandidateId]) REFERENCES [Candidates] ([CandidateId]) ON DELETE NO ACTION
);

CREATE TABLE [JobSkills] (
    [JobId] int NOT NULL,
    [SkillCode] nvarchar(50) NOT NULL,
    [DisplayName] nvarchar(100) NOT NULL,
    [SkillType] nvarchar(20) NOT NULL,
    CONSTRAINT [PK_JobSkills] PRIMARY KEY ([JobId], [SkillCode]),
    CONSTRAINT [CK_JobSkills_SkillType] CHECK (SkillType IN (N'MustHave', N'NiceToHave')),
    CONSTRAINT [FK_JobSkills_Jobs] FOREIGN KEY ([JobId]) REFERENCES [Jobs] ([JobId]) ON DELETE NO ACTION
);

CREATE TABLE [EvaluationRuns] (
    [RunId] int NOT NULL IDENTITY,
    [RunType] nvarchar(20) NOT NULL,
    [PromptVersionId] int NOT NULL,
    [EvaluationSetId] int NULL,
    [ModelName] nvarchar(100) NOT NULL,
    [Temperature] decimal(3,2) NOT NULL,
    [KeywordWeight] decimal(3,2) NOT NULL,
    [AiWeight] decimal(3,2) NOT NULL,
    [PassThreshold] decimal(5,2) NOT NULL CONSTRAINT [DF_EvaluationRuns_PassThreshold] DEFAULT 70.0,
    [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_EvaluationRuns_Status] DEFAULT N'Queued',
    [TotalItems] int NOT NULL,
    [ProcessedItems] int NOT NULL CONSTRAINT [DF_EvaluationRuns_ProcessedItems] DEFAULT 0,
    [FailedItems] int NOT NULL CONSTRAINT [DF_EvaluationRuns_FailedItems] DEFAULT 0,
    [AccuracyPercent] decimal(5,2) NULL,
    [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_EvaluationRuns_CreatedAt] DEFAULT (SYSUTCDATETIME()),
    [CompletedAt] datetime2 NULL,
    CONSTRAINT [PK_EvaluationRuns] PRIMARY KEY ([RunId]),
    CONSTRAINT [CK_EvaluationRuns_AccuracyPercent] CHECK (AccuracyPercent BETWEEN 0 AND 100),
    CONSTRAINT [CK_EvaluationRuns_AiWeight] CHECK (AiWeight BETWEEN 0 AND 1),
    CONSTRAINT [CK_EvaluationRuns_FailedItems] CHECK (FailedItems >= 0),
    CONSTRAINT [CK_EvaluationRuns_KeywordWeight] CHECK (KeywordWeight BETWEEN 0 AND 1),
    CONSTRAINT [CK_EvaluationRuns_PassThreshold] CHECK (PassThreshold BETWEEN 0 AND 100),
    CONSTRAINT [CK_EvaluationRuns_ProcessedItems] CHECK (ProcessedItems >= 0),
    CONSTRAINT [CK_EvaluationRuns_RunType] CHECK (RunType IN (N'Screening', N'PromptTest')),
    CONSTRAINT [CK_EvaluationRuns_Set] CHECK ((RunType = N'PromptTest' AND EvaluationSetId IS NOT NULL) OR (RunType = N'Screening' AND EvaluationSetId IS NULL)),
    CONSTRAINT [CK_EvaluationRuns_Status] CHECK (Status IN (N'Queued', N'Running', N'Completed', N'Failed')),
    CONSTRAINT [CK_EvaluationRuns_Temperature] CHECK (Temperature >= 0),
    CONSTRAINT [CK_EvaluationRuns_TotalItems] CHECK (TotalItems >= 0),
    CONSTRAINT [CK_EvaluationRuns_Weights] CHECK (KeywordWeight + AiWeight = 1),
    CONSTRAINT [FK_EvaluationRuns_EvaluationSets] FOREIGN KEY ([EvaluationSetId]) REFERENCES [EvaluationSets] ([EvaluationSetId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_EvaluationRuns_PromptVersions] FOREIGN KEY ([PromptVersionId]) REFERENCES [PromptVersions] ([PromptVersionId]) ON DELETE NO ACTION
);

CREATE TABLE [Applications] (
    [ApplicationId] int NOT NULL IDENTITY,
    [CvId] uniqueidentifier NOT NULL,
    [JobId] int NOT NULL,
    [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_Applications_CreatedAt] DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT [PK_Applications] PRIMARY KEY ([ApplicationId]),
    CONSTRAINT [UQ_Applications_Cv_Job] UNIQUE ([CvId], [JobId]),
    CONSTRAINT [FK_Applications_Cvs] FOREIGN KEY ([CvId]) REFERENCES [Cvs] ([CvId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Applications_Jobs] FOREIGN KEY ([JobId]) REFERENCES [Jobs] ([JobId]) ON DELETE NO ACTION
);

CREATE TABLE [JobSkillAliases] (
    [JobId] int NOT NULL,
    [SkillCode] nvarchar(50) NOT NULL,
    [Alias] nvarchar(100) NOT NULL,
    CONSTRAINT [PK_JobSkillAliases] PRIMARY KEY ([JobId], [SkillCode], [Alias]),
    CONSTRAINT [FK_JobSkillAliases_JobSkills] FOREIGN KEY ([JobId], [SkillCode]) REFERENCES [JobSkills] ([JobId], [SkillCode]) ON DELETE NO ACTION
);

CREATE TABLE [Evaluations] (
    [EvaluationId] int NOT NULL IDENTITY,
    [RunId] int NOT NULL,
    [ApplicationId] int NOT NULL,
    [KeywordScore] decimal(5,2) NULL,
    [AiScore] decimal(5,2) NULL,
    [FinalScore] decimal(5,2) NULL,
    [ScoreSource] nvarchar(20) NULL,
    [FitLabel] nvarchar(30) NULL,
    [PredictedPass] bit NULL,
    [YearsExperience] decimal(4,1) NULL,
    [Reasoning] nvarchar(max) NULL,
    [AiRawResponse] nvarchar(max) NULL,
    [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_Evaluations_Status] DEFAULT N'Pending',
    [ErrorMessage] nvarchar(1000) NULL,
    [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_Evaluations_CreatedAt] DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT [PK_Evaluations] PRIMARY KEY ([EvaluationId]),
    CONSTRAINT [UQ_Evaluations_Run_Application] UNIQUE ([RunId], [ApplicationId]),
    CONSTRAINT [CK_Evaluations_AiScore] CHECK (AiScore BETWEEN 0 AND 100),
    CONSTRAINT [CK_Evaluations_FinalScore] CHECK (FinalScore BETWEEN 0 AND 100),
    CONSTRAINT [CK_Evaluations_KeywordScore] CHECK (KeywordScore BETWEEN 0 AND 100),
    CONSTRAINT [CK_Evaluations_ScoreSource] CHECK (ScoreSource IN (N'Combined', N'KeywordOnly')),
    CONSTRAINT [CK_Evaluations_Status] CHECK (Status IN (N'Pending', N'Completed', N'Failed')),
    CONSTRAINT [CK_Evaluations_YearsExperience] CHECK (YearsExperience >= 0),
    CONSTRAINT [FK_Evaluations_Applications] FOREIGN KEY ([ApplicationId]) REFERENCES [Applications] ([ApplicationId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Evaluations_EvaluationRuns] FOREIGN KEY ([RunId]) REFERENCES [EvaluationRuns] ([RunId]) ON DELETE NO ACTION
);

CREATE TABLE [HrLabels] (
    [EvaluationSetId] int NOT NULL,
    [ApplicationId] int NOT NULL,
    [IsPass] bit NOT NULL,
    [Note] nvarchar(500) NULL,
    [LabeledBy] nvarchar(100) NOT NULL,
    [LabeledAt] datetime2 NOT NULL CONSTRAINT [DF_HrLabels_LabeledAt] DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT [PK_HrLabels] PRIMARY KEY ([EvaluationSetId], [ApplicationId]),
    CONSTRAINT [FK_HrLabels_Applications] FOREIGN KEY ([ApplicationId]) REFERENCES [Applications] ([ApplicationId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_HrLabels_EvaluationSets] FOREIGN KEY ([EvaluationSetId]) REFERENCES [EvaluationSets] ([EvaluationSetId]) ON DELETE NO ACTION
);

CREATE TABLE [EvaluationSkills] (
    [EvaluationId] int NOT NULL,
    [JobId] int NOT NULL,
    [SkillCode] nvarchar(50) NOT NULL,
    [IsMatched] bit NOT NULL,
    [Evidence] nvarchar(500) NULL,
    CONSTRAINT [PK_EvaluationSkills] PRIMARY KEY ([EvaluationId], [JobId], [SkillCode]),
    CONSTRAINT [FK_EvaluationSkills_Evaluations] FOREIGN KEY ([EvaluationId]) REFERENCES [Evaluations] ([EvaluationId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_EvaluationSkills_JobSkills] FOREIGN KEY ([JobId], [SkillCode]) REFERENCES [JobSkills] ([JobId], [SkillCode]) ON DELETE NO ACTION
);

CREATE INDEX [IX_Applications_JobId] ON [Applications] ([JobId]);

CREATE INDEX [IX_Cvs_CandidateId] ON [Cvs] ([CandidateId]);

CREATE INDEX [IX_EvaluationRuns_EvaluationSetId] ON [EvaluationRuns] ([EvaluationSetId]);

CREATE INDEX [IX_EvaluationRuns_PromptVersionId] ON [EvaluationRuns] ([PromptVersionId]);

CREATE INDEX [IX_Evaluations_ApplicationId] ON [Evaluations] ([ApplicationId]);

CREATE INDEX [IX_Evaluations_Run_FinalScore] ON [Evaluations] ([RunId], [FinalScore] DESC);

CREATE INDEX [IX_EvaluationSkills_JobSkill] ON [EvaluationSkills] ([JobId], [SkillCode]);

CREATE INDEX [IX_HrLabels_ApplicationId] ON [HrLabels] ([ApplicationId]);

CREATE UNIQUE INDEX [UX_PromptVersions_OneActive] ON [PromptVersions] ([IsActive]) WHERE [IsActive] = 1;

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261106132922_InitialCreate', N'10.0.14');

COMMIT;
GO

