/* Read-only runtime audit. Execute against the intended database; no candidate content is selected. */
SELECT MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory ORDER BY MigrationId;

SELECT expected.Name AS ExpectedColumn, c.name AS ActualColumn, TYPE_NAME(c.user_type_id) AS SqlType,
       c.max_length, c.is_nullable, d.definition AS DefaultDefinition
FROM (VALUES (N'Stage'), (N'RejectReason'), (N'StageUpdatedAt'), (N'StageVersion')) expected(Name)
LEFT JOIN sys.columns c ON c.object_id = OBJECT_ID(N'dbo.Applications') AND c.name = expected.Name
LEFT JOIN sys.default_constraints d ON d.object_id = c.default_object_id;

SELECT N'ApplicationEvents' AS ExpectedTable, OBJECT_ID(N'dbo.ApplicationEvents', N'U') AS ObjectId;
SELECT name, is_disabled, is_not_trusted, definition FROM sys.check_constraints
WHERE parent_object_id IN (OBJECT_ID(N'dbo.Applications'), OBJECT_ID(N'dbo.ApplicationEvents'));
SELECT name, is_disabled FROM sys.indexes
WHERE object_id IN (OBJECT_ID(N'dbo.Applications'), OBJECT_ID(N'dbo.ApplicationEvents')) AND name IS NOT NULL;
SELECT name, is_disabled, is_instead_of_trigger FROM sys.triggers
WHERE object_id = OBJECT_ID(N'dbo.TR_ApplicationEvents_NoChange');
SELECT name, delete_referential_action_desc, is_disabled, is_not_trusted FROM sys.foreign_keys
WHERE parent_object_id = OBJECT_ID(N'dbo.ApplicationEvents');

DECLARE @RunId int = 25;
SELECT RunId, RunType, Status, TotalItems, ProcessedItems, FailedItems
FROM dbo.EvaluationRuns WHERE RunId = @RunId;
SELECT RunId, Status, COUNT(*) AS EvaluationCount
FROM dbo.Evaluations WHERE RunId = @RunId GROUP BY RunId, Status;
