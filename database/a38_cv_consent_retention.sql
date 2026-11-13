-- A38: generated from ApplicationStages to CvConsentRetention; review/approve before deployment.
-- Run only after a verified backup and ApplicationStages (20261112045258) are in place.
-- Nullable additions preserve legacy rows. No consent/source is inferred for old CVs.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
GO

IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20261112045258_ApplicationStages')
    THROW 51038, 'Apply and verify ApplicationStages before running the A38 migration.', 1;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113014746_CvConsentRetention'
)
BEGIN
    ALTER TABLE [Cvs] ADD [AnonymizedAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113014746_CvConsentRetention'
)
BEGIN
    ALTER TABLE [Cvs] ADD [ConsentConfirmedAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113014746_CvConsentRetention'
)
BEGIN
    ALTER TABLE [Cvs] ADD [RetainUntil] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113014746_CvConsentRetention'
)
BEGIN
    ALTER TABLE [Cvs] ADD [SourceChannel] nvarchar(30) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113014746_CvConsentRetention'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Cvs_RetainUntil] ON [Cvs] ([RetainUntil]) WHERE [AnonymizedAt] IS NULL AND [RetainUntil] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113014746_CvConsentRetention'
)
BEGIN
    EXEC(N'ALTER TABLE [Cvs] ADD CONSTRAINT [CK_Cvs_SourceChannel] CHECK (SourceChannel IS NULL OR (SourceChannel COLLATE Latin1_General_100_BIN2 IN (N''Email'', N''Website'', N''JobBoard'', N''Referral'', N''Other'') AND DATALENGTH(SourceChannel) = DATALENGTH(RTRIM(SourceChannel))))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113014746_CvConsentRetention'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261113014746_CvConsentRetention', N'10.0.15');
END;

COMMIT;
GO
