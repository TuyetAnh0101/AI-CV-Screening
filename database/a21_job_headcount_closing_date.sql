SET XACT_ABORT ON;
BEGIN TRY
BEGIN TRANSACTION;
ALTER TABLE [Jobs] ADD [ClosingDate] date NULL;

ALTER TABLE [Jobs] ADD [Headcount] int NULL;

-- Defer constraint binding until the preceding ADD COLUMN statements have completed.
EXEC(N'ALTER TABLE [Jobs] ADD CONSTRAINT [CK_Jobs_Headcount] CHECK (Headcount IS NULL OR Headcount BETWEEN 1 AND 1000);');

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261117045748_JobHeadcountClosingDate', N'10.0.15');

COMMIT;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
