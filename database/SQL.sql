IF DB_ID(N'SmartCvDb') IS NULL
    CREATE DATABASE SmartCvDb;
GO

USE SmartCvDb;
GO

CREATE TABLE Candidates
(
    CandidateId INT IDENTITY(1,1) NOT NULL,
    FullName NVARCHAR(150) NOT NULL,
    Email NVARCHAR(254) NULL,
    PhoneNumber CHAR(10) NULL,
    CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Candidates_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_Candidates PRIMARY KEY (CandidateId),
    CONSTRAINT CK_Candidates_PhoneNumber CHECK (PhoneNumber NOT LIKE '%[^0-9]%')
);
GO

CREATE TABLE Jobs
(
    JobId INT IDENTITY(1,1) NOT NULL,
    Title NVARCHAR(150) NOT NULL,
    Description NVARCHAR(MAX) NULL,
    MinGpa DECIMAL(3,2) NULL,
    MinYearsExperience DECIMAL(4,1) NOT NULL CONSTRAINT DF_Jobs_MinYearsExperience DEFAULT 0,
    IsActive BIT NOT NULL CONSTRAINT DF_Jobs_IsActive DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Jobs_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_Jobs PRIMARY KEY (JobId),
    CONSTRAINT UQ_Jobs_Title UNIQUE (Title),
    CONSTRAINT CK_Jobs_MinGpa CHECK (MinGpa BETWEEN 0 AND 4),
    CONSTRAINT CK_Jobs_MinYearsExperience CHECK (MinYearsExperience >= 0)
);
GO

CREATE TABLE JobSkills
(
    JobId INT NOT NULL,
    SkillCode NVARCHAR(50) NOT NULL,
    DisplayName NVARCHAR(100) NOT NULL,
    SkillType NVARCHAR(20) NOT NULL,
    CONSTRAINT PK_JobSkills PRIMARY KEY (JobId, SkillCode),
    CONSTRAINT FK_JobSkills_Jobs FOREIGN KEY (JobId) REFERENCES Jobs(JobId),
    CONSTRAINT CK_JobSkills_SkillType CHECK (SkillType IN (N'MustHave', N'NiceToHave'))
);
GO

CREATE TABLE JobSkillAliases
(
    JobId INT NOT NULL,
    SkillCode NVARCHAR(50) NOT NULL,
    Alias NVARCHAR(100) NOT NULL,
    CONSTRAINT PK_JobSkillAliases PRIMARY KEY (JobId, SkillCode, Alias),
    CONSTRAINT FK_JobSkillAliases_JobSkills FOREIGN KEY (JobId, SkillCode)
        REFERENCES JobSkills(JobId, SkillCode)
);
GO

CREATE TABLE Cvs
(
    CvId UNIQUEIDENTIFIER NOT NULL,
    CandidateId INT NULL,
    OriginalFileName NVARCHAR(255) NOT NULL,
    StoredPath NVARCHAR(500) NOT NULL,
    ContentType NVARCHAR(100) NOT NULL,
    FileSizeBytes BIGINT NOT NULL,
    FileHash CHAR(64) NOT NULL,
    ExtractedText NVARCHAR(MAX) NULL,
    ExtractionMethod NVARCHAR(20) NULL,
    ExtractionStatus NVARCHAR(20) NOT NULL CONSTRAINT DF_Cvs_ExtractionStatus DEFAULT N'Pending',
    ExtractionError NVARCHAR(1000) NULL,
    UploadedAt DATETIME2 NOT NULL CONSTRAINT DF_Cvs_UploadedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_Cvs PRIMARY KEY (CvId),
    CONSTRAINT FK_Cvs_Candidates FOREIGN KEY (CandidateId) REFERENCES Candidates(CandidateId),
    CONSTRAINT UQ_Cvs_FileHash UNIQUE (FileHash),
    CONSTRAINT CK_Cvs_FileSizeBytes CHECK (FileSizeBytes > 0),
    CONSTRAINT CK_Cvs_ExtractionMethod CHECK (ExtractionMethod IN (N'PdfText', N'PdfOcr', N'ImageOcr')),
    CONSTRAINT CK_Cvs_ExtractionStatus CHECK (ExtractionStatus IN (N'Pending', N'Completed', N'Failed'))
);
GO

CREATE TABLE Applications
(
    ApplicationId INT IDENTITY(1,1) NOT NULL,
    CvId UNIQUEIDENTIFIER NOT NULL,
    JobId INT NOT NULL,
    CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Applications_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_Applications PRIMARY KEY (ApplicationId),
    CONSTRAINT FK_Applications_Cvs FOREIGN KEY (CvId) REFERENCES Cvs(CvId),
    CONSTRAINT FK_Applications_Jobs FOREIGN KEY (JobId) REFERENCES Jobs(JobId),
    CONSTRAINT UQ_Applications_Cv_Job UNIQUE (CvId, JobId)
);
GO

CREATE TABLE PromptVersions
(
    PromptVersionId INT IDENTITY(1,1) NOT NULL,
    VersionName NVARCHAR(100) NOT NULL,
    SystemPrompt NVARCHAR(MAX) NOT NULL,
    ChangeNote NVARCHAR(500) NULL,
    IsActive BIT NOT NULL CONSTRAINT DF_PromptVersions_IsActive DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_PromptVersions_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_PromptVersions PRIMARY KEY (PromptVersionId),
    CONSTRAINT UQ_PromptVersions_VersionName UNIQUE (VersionName)
);
GO

CREATE TABLE EvaluationSets
(
    EvaluationSetId INT IDENTITY(1,1) NOT NULL,
    Name NVARCHAR(150) NOT NULL,
    Description NVARCHAR(500) NULL,
    CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_EvaluationSets_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_EvaluationSets PRIMARY KEY (EvaluationSetId),
    CONSTRAINT UQ_EvaluationSets_Name UNIQUE (Name)
);
GO

CREATE TABLE HrLabels
(
    EvaluationSetId INT NOT NULL,
    ApplicationId INT NOT NULL,
    IsPass BIT NOT NULL,
    Note NVARCHAR(500) NULL,
    LabeledBy NVARCHAR(100) NOT NULL,
    LabeledAt DATETIME2 NOT NULL CONSTRAINT DF_HrLabels_LabeledAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_HrLabels PRIMARY KEY (EvaluationSetId, ApplicationId),
    CONSTRAINT FK_HrLabels_EvaluationSets FOREIGN KEY (EvaluationSetId) REFERENCES EvaluationSets(EvaluationSetId),
    CONSTRAINT FK_HrLabels_Applications FOREIGN KEY (ApplicationId) REFERENCES Applications(ApplicationId)
);
GO

CREATE TABLE EvaluationRuns
(
    RunId INT IDENTITY(1,1) NOT NULL,
    RunType NVARCHAR(20) NOT NULL,
    PromptVersionId INT NOT NULL,
    EvaluationSetId INT NULL,
    ModelName NVARCHAR(100) NOT NULL,
    Temperature DECIMAL(3,2) NOT NULL,
    KeywordWeight DECIMAL(3,2) NOT NULL,
    AiWeight DECIMAL(3,2) NOT NULL,
    PassThreshold DECIMAL(5,2) NOT NULL CONSTRAINT DF_EvaluationRuns_PassThreshold DEFAULT 70,
    Status NVARCHAR(20) NOT NULL CONSTRAINT DF_EvaluationRuns_Status DEFAULT N'Queued',
    TotalItems INT NOT NULL,
    ProcessedItems INT NOT NULL CONSTRAINT DF_EvaluationRuns_ProcessedItems DEFAULT 0,
    FailedItems INT NOT NULL CONSTRAINT DF_EvaluationRuns_FailedItems DEFAULT 0,
    AccuracyPercent DECIMAL(5,2) NULL,
    CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_EvaluationRuns_CreatedAt DEFAULT SYSUTCDATETIME(),
    CompletedAt DATETIME2 NULL,
    CONSTRAINT PK_EvaluationRuns PRIMARY KEY (RunId),
    CONSTRAINT FK_EvaluationRuns_PromptVersions FOREIGN KEY (PromptVersionId) REFERENCES PromptVersions(PromptVersionId),
    CONSTRAINT FK_EvaluationRuns_EvaluationSets FOREIGN KEY (EvaluationSetId) REFERENCES EvaluationSets(EvaluationSetId),
    CONSTRAINT CK_EvaluationRuns_RunType CHECK (RunType IN (N'Screening', N'PromptTest')),
    CONSTRAINT CK_EvaluationRuns_Status CHECK (Status IN (N'Queued', N'Running', N'Completed', N'Failed')),
    CONSTRAINT CK_EvaluationRuns_Temperature CHECK (Temperature >= 0),
    CONSTRAINT CK_EvaluationRuns_KeywordWeight CHECK (KeywordWeight BETWEEN 0 AND 1),
    CONSTRAINT CK_EvaluationRuns_AiWeight CHECK (AiWeight BETWEEN 0 AND 1),
    CONSTRAINT CK_EvaluationRuns_Weights CHECK (KeywordWeight + AiWeight = 1),
    CONSTRAINT CK_EvaluationRuns_PassThreshold CHECK (PassThreshold BETWEEN 0 AND 100),
    CONSTRAINT CK_EvaluationRuns_TotalItems CHECK (TotalItems >= 0),
    CONSTRAINT CK_EvaluationRuns_ProcessedItems CHECK (ProcessedItems >= 0),
    CONSTRAINT CK_EvaluationRuns_FailedItems CHECK (FailedItems >= 0),
    CONSTRAINT CK_EvaluationRuns_AccuracyPercent CHECK (AccuracyPercent BETWEEN 0 AND 100),
    CONSTRAINT CK_EvaluationRuns_Set CHECK (
        (RunType = N'PromptTest' AND EvaluationSetId IS NOT NULL)
        OR (RunType = N'Screening' AND EvaluationSetId IS NULL)
    )
);
GO

CREATE TABLE Evaluations
(
    EvaluationId INT IDENTITY(1,1) NOT NULL,
    RunId INT NOT NULL,
    ApplicationId INT NOT NULL,
    KeywordScore DECIMAL(5,2) NULL,
    AiScore DECIMAL(5,2) NULL,
    FinalScore DECIMAL(5,2) NULL,
    ScoreSource NVARCHAR(20) NULL,
    FitLabel NVARCHAR(30) NULL,
    PredictedPass BIT NULL,
    YearsExperience DECIMAL(4,1) NULL,
    Reasoning NVARCHAR(MAX) NULL,
    AiRawResponse NVARCHAR(MAX) NULL,
    Status NVARCHAR(20) NOT NULL CONSTRAINT DF_Evaluations_Status DEFAULT N'Pending',
    ErrorMessage NVARCHAR(1000) NULL,
    CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Evaluations_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_Evaluations PRIMARY KEY (EvaluationId),
    CONSTRAINT FK_Evaluations_EvaluationRuns FOREIGN KEY (RunId) REFERENCES EvaluationRuns(RunId),
    CONSTRAINT FK_Evaluations_Applications FOREIGN KEY (ApplicationId) REFERENCES Applications(ApplicationId),
    CONSTRAINT UQ_Evaluations_Run_Application UNIQUE (RunId, ApplicationId),
    CONSTRAINT CK_Evaluations_KeywordScore CHECK (KeywordScore BETWEEN 0 AND 100),
    CONSTRAINT CK_Evaluations_AiScore CHECK (AiScore BETWEEN 0 AND 100),
    CONSTRAINT CK_Evaluations_FinalScore CHECK (FinalScore BETWEEN 0 AND 100),
    CONSTRAINT CK_Evaluations_ScoreSource CHECK (ScoreSource IN (N'Combined', N'KeywordOnly')),
    CONSTRAINT CK_Evaluations_YearsExperience CHECK (YearsExperience >= 0),
    CONSTRAINT CK_Evaluations_Status CHECK (Status IN (N'Pending', N'Completed', N'Failed'))
);
GO

CREATE TABLE EvaluationSkills
(
    EvaluationId INT NOT NULL,
    JobId INT NOT NULL,
    SkillCode NVARCHAR(50) NOT NULL,
    IsMatched BIT NOT NULL,
    Evidence NVARCHAR(500) NULL,
    CONSTRAINT PK_EvaluationSkills PRIMARY KEY (EvaluationId, JobId, SkillCode),
    CONSTRAINT FK_EvaluationSkills_Evaluations FOREIGN KEY (EvaluationId) REFERENCES Evaluations(EvaluationId),
    CONSTRAINT FK_EvaluationSkills_JobSkills FOREIGN KEY (JobId, SkillCode) REFERENCES JobSkills(JobId, SkillCode)
);
GO

-- Chi cho phep mot prompt dang duoc su dung
CREATE UNIQUE INDEX UX_PromptVersions_OneActive
    ON PromptVersions(IsActive)
    WHERE IsActive = 1;

CREATE INDEX IX_Cvs_CandidateId ON Cvs(CandidateId);
CREATE INDEX IX_Applications_JobId ON Applications(JobId);
CREATE INDEX IX_HrLabels_ApplicationId ON HrLabels(ApplicationId);
CREATE INDEX IX_EvaluationRuns_PromptVersionId ON EvaluationRuns(PromptVersionId);
CREATE INDEX IX_EvaluationRuns_EvaluationSetId ON EvaluationRuns(EvaluationSetId);
CREATE INDEX IX_Evaluations_ApplicationId ON Evaluations(ApplicationId);
CREATE INDEX IX_Evaluations_Run_FinalScore ON Evaluations(RunId, FinalScore DESC);
CREATE INDEX IX_EvaluationSkills_JobSkill ON EvaluationSkills(JobId, SkillCode);
GO