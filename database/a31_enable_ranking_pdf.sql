-- OPTIONAL manual alternative to the EF migration. DO NOT run both.
-- Update @ExpectedDatabase for the approved catalog; requires A31 ExportLogs already applied.
-- Do not paste this file into random SQL connections. Run only after backup/review.
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @ExpectedDatabase sysname = N'REVIEW_DATABASE_NAME_BEFORE_EXECUTION';
IF DB_NAME() <> @ExpectedDatabase
    THROW 51032, N'Sai database. Dung lai va xac nhan database dang su dung.', 1;
IF OBJECT_ID(N'dbo.ExportLogs', N'U') IS NULL OR OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 51032, N'Chua co ExportLogs hoac migration history.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = '20261120114152_AddRankingExportLogs')
    THROW 51032, N'Chua ap dung A31.', 1;
IF EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = '20261124223000_AllowRankingPdfExport')
BEGIN
    PRINT N'Migration da duoc ap dung, khong thay doi.';
END
ELSE
BEGIN
    BEGIN TRANSACTION;
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.ExportLogs') AND name = N'CK_ExportLogs_Format')
        THROW 51032, N'Khong tim thay check constraint cu; dung lai de dieu tra.', 1;
    ALTER TABLE dbo.ExportLogs DROP CONSTRAINT CK_ExportLogs_Format;
    ALTER TABLE dbo.ExportLogs WITH CHECK ADD CONSTRAINT CK_ExportLogs_Format
        CHECK ([Format] COLLATE Latin1_General_100_BIN2 IN ('xlsx','pdf'));
    INSERT dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
        VALUES (N'20261124223000_AllowRankingPdfExport', N'10.0.15');
    COMMIT TRANSACTION;
END;
GO
