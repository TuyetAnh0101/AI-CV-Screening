/* A51: read-only verification after applying a51_add_workspaces.sql (or `dotnet ef database update`).
   Only SELECTs (plus a session temp table #A51). Never changes application data, Users, triggers or constraints.
   Result: one row per check (PASS / FAIL / INFO). The script raises error 52051 when any check FAILs,
   so `sqlcmd -b` returns a non-zero exit code.

   Run (example):  sqlcmd -S localhost\BB -d <catalog> -E -C -I -f 65001 -b -i database\check_a51_workspaces.sql */
SET NOCOUNT ON;

IF OBJECT_ID(N'tempdb..#A51') IS NOT NULL DROP TABLE #A51;
CREATE TABLE #A51 (Id int IDENTITY(1,1), CheckName nvarchar(120) NOT NULL, Status varchar(4) NOT NULL, Detail nvarchar(400) NULL);

-- 1. Migration history + new tables
INSERT #A51 (CheckName, Status, Detail)
SELECT N'migration AddWorkspaces recorded',
       CASE WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]AddWorkspaces') THEN 'PASS' ELSE 'FAIL' END,
       (SELECT TOP (1) MigrationId FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]AddWorkspaces');

INSERT #A51 (CheckName, Status, Detail)
SELECT N'table ' + t.Name, CASE WHEN OBJECT_ID(N'dbo.' + t.Name, N'U') IS NULL THEN 'FAIL' ELSE 'PASS' END, NULL
FROM (VALUES (N'Workspaces'), (N'WorkspaceMembers'), (N'UserTokens'), (N'UserExternalLogins')) t(Name);

-- 2. WorkspaceId columns: required (NOT NULL) except AuditEvents (nullable by design)
INSERT #A51 (CheckName, Status, Detail)
SELECT N'column ' + r.TableName + N'.WorkspaceId ' + CASE WHEN r.Required = 1 THEN N'NOT NULL' ELSE N'NULL allowed' END,
       CASE WHEN c.column_id IS NULL THEN 'FAIL'
            WHEN r.Required = 1 AND c.is_nullable = 1 THEN 'FAIL'
            WHEN r.Required = 0 AND c.is_nullable = 0 THEN 'FAIL' ELSE 'PASS' END, NULL
FROM (VALUES (N'Jobs', 1), (N'Cvs', 1), (N'Candidates', 1), (N'Applications', 1), (N'PromptVersions', 1),
             (N'EvaluationSets', 1), (N'EvaluationRuns', 1), (N'ExportLogs', 1), (N'AuditEvents', 0)) r(TableName, Required)
LEFT JOIN sys.columns c ON c.object_id = OBJECT_ID(N'dbo.' + r.TableName) AND c.name = N'WorkspaceId';

-- 3. Row counts per workspace (informational) and rows without a workspace (must be 0, except AuditEvents)
IF OBJECT_ID(N'dbo.Workspaces', N'U') IS NOT NULL
BEGIN
    DECLARE @t sysname, @sql nvarchar(max);
    DECLARE tables_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT Name FROM (VALUES (N'Jobs'), (N'Cvs'), (N'Candidates'), (N'Applications'), (N'PromptVersions'),
                                 (N'EvaluationSets'), (N'EvaluationRuns'), (N'ExportLogs'), (N'AuditEvents')) x(Name);
    OPEN tables_cursor; FETCH NEXT FROM tables_cursor INTO @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF COL_LENGTH(N'dbo.' + @t, N'WorkspaceId') IS NOT NULL
        BEGIN
            SET @sql = N'INSERT #A51 (CheckName, Status, Detail)
                SELECT N''rows without workspace in ' + @t + N''',
                       CASE WHEN COUNT(*) = 0 OR N''' + @t + N''' = N''AuditEvents'' THEN CASE WHEN COUNT(*) = 0 THEN ''PASS'' ELSE ''INFO'' END ELSE ''FAIL'' END,
                       CAST(COUNT(*) AS nvarchar(20)) FROM dbo.' + QUOTENAME(@t) + N' WHERE WorkspaceId IS NULL;
                INSERT #A51 (CheckName, Status, Detail)
                SELECT N''rows in ' + @t + N' by workspace'', ''INFO'',
                       ISNULL((SELECT STRING_AGG(CONCAT(N''ws='', ISNULL(CAST(WorkspaceId AS nvarchar(20)), N''NULL''), N'': '', n), N'', '')
                               FROM (SELECT WorkspaceId, COUNT(*) AS n FROM dbo.' + QUOTENAME(@t) + N' GROUP BY WorkspaceId) g), N''(empty)'');';
            EXEC sys.sp_executesql @sql;
        END
        FETCH NEXT FROM tables_cursor INTO @t;
    END
    CLOSE tables_cursor; DEALLOCATE tables_cursor;
END

-- 4. Internal workspace 1, owner and membership
IF OBJECT_ID(N'dbo.Workspaces', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.WorkspaceMembers', N'U') IS NOT NULL
BEGIN
    EXEC sys.sp_executesql N'
    INSERT #A51 (CheckName, Status, Detail)
    SELECT N''workspace 1 is the active Internal workspace'',
           CASE WHEN EXISTS (SELECT 1 FROM dbo.Workspaces WHERE WorkspaceId = 1 AND Kind = ''Internal'' AND IsActive = 1) THEN ''PASS'' ELSE ''FAIL'' END,
           (SELECT TOP (1) CONCAT(Name, N'' / owner='', ISNULL(CAST(OwnerUserId AS nvarchar(20)), N''NULL'')) FROM dbo.Workspaces WHERE WorkspaceId = 1);

    INSERT #A51 (CheckName, Status, Detail)
    SELECT N''workspace 1 owner = smallest Admin UserId'',
           CASE WHEN NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Role = N''Admin'') THEN ''INFO''
                WHEN (SELECT OwnerUserId FROM dbo.Workspaces WHERE WorkspaceId = 1) = (SELECT MIN(UserId) FROM dbo.Users WHERE Role = N''Admin'') THEN ''PASS'' ELSE ''FAIL'' END,
           CONCAT(N''minAdmin='', ISNULL(CAST((SELECT MIN(UserId) FROM dbo.Users WHERE Role = N''Admin'') AS nvarchar(20)), N''none''));

    INSERT #A51 (CheckName, Status, Detail)
    SELECT N''users without a workspace-1 membership (pre-A51 accounts)'',
           CASE WHEN COUNT(*) = 0 THEN ''PASS'' ELSE ''FAIL'' END, CAST(COUNT(*) AS nvarchar(20))
    FROM dbo.Users u WHERE NOT EXISTS (SELECT 1 FROM dbo.WorkspaceMembers m WHERE m.WorkspaceId = 1 AND m.UserId = u.UserId);

    INSERT #A51 (CheckName, Status, Detail)
    SELECT N''users with more than one membership (MVP: one workspace per account)'',
           CASE WHEN COUNT(*) = 0 THEN ''PASS'' ELSE ''INFO'' END, CAST(COUNT(*) AS nvarchar(20))
    FROM (SELECT UserId FROM dbo.WorkspaceMembers GROUP BY UserId HAVING COUNT(*) > 1) x;

    INSERT #A51 (CheckName, Status, Detail)
    SELECT N''workspaces with more than one Owner'', CASE WHEN COUNT(*) = 0 THEN ''PASS'' ELSE ''FAIL'' END, CAST(COUNT(*) AS nvarchar(20))
    FROM (SELECT WorkspaceId FROM dbo.WorkspaceMembers WHERE Role = ''Owner'' GROUP BY WorkspaceId HAVING COUNT(*) > 1) x;

    INSERT #A51 (CheckName, Status, Detail)
    SELECT N''Users.Role never replaced by Owner/Member'',
           CASE WHEN COUNT(*) = 0 THEN ''PASS'' ELSE ''FAIL'' END, CAST(COUNT(*) AS nvarchar(20))
    FROM dbo.Users WHERE Role NOT IN (N''Admin'', N''HR'');';
END

-- 5. Users: new columns (only new columns were backfilled)
IF COL_LENGTH(N'dbo.Users', N'EmailVerifiedAt') IS NOT NULL AND COL_LENGTH(N'dbo.Users', N'SignupSource') IS NOT NULL
BEGIN
    EXEC sys.sp_executesql N'
    INSERT #A51 (CheckName, Status, Detail)
    SELECT N''pre-A51 accounts (SignupSource = Admin) have EmailVerifiedAt'',
           CASE WHEN COUNT(*) = 0 THEN ''PASS'' ELSE ''FAIL'' END, CAST(COUNT(*) AS nvarchar(20))
    FROM dbo.Users WHERE SignupSource = N''Admin'' AND EmailVerifiedAt IS NULL;';
END

-- 6. Legacy global constraints are gone; workspace-scoped replacements exist
INSERT #A51 (CheckName, Status, Detail)
SELECT N'legacy global constraint removed: ' + l.Name,
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes i WHERE i.name = l.Name AND i.object_id = OBJECT_ID(N'dbo.' + l.TableName)) THEN 'FAIL' ELSE 'PASS' END, NULL
FROM (VALUES (N'UQ_Jobs_Title', N'Jobs'), (N'UQ_Cvs_FileHash', N'Cvs'), (N'UQ_PromptVersions_VersionName', N'PromptVersions'),
             (N'UQ_EvaluationSets_Name', N'EvaluationSets'), (N'UX_PromptVersions_OneActive', N'PromptVersions')) l(Name, TableName);

INSERT #A51 (CheckName, Status, Detail)
SELECT N'index/constraint present: ' + r.Name,
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes i WHERE i.name = r.Name AND i.object_id = OBJECT_ID(N'dbo.' + r.TableName)
                         AND i.is_disabled = 0 AND i.is_unique = r.IsUnique) THEN 'PASS' ELSE 'FAIL' END, NULL
FROM (VALUES (N'UQ_Jobs_Workspace_Title', N'Jobs', 1), (N'AK_Jobs_Workspace_Job', N'Jobs', 1),
             (N'AK_Cvs_Workspace_Cv', N'Cvs', 1), (N'IX_Cvs_Workspace_FileHash', N'Cvs', 1),
             (N'AK_Candidates_Workspace_Candidate', N'Candidates', 1),
             (N'AK_PromptVersions_Workspace_Prompt', N'PromptVersions', 1), (N'UQ_PromptVersions_Workspace_VersionName', N'PromptVersions', 1),
             (N'UX_PromptVersions_Workspace_OneActive', N'PromptVersions', 1),
             (N'AK_EvaluationSets_Workspace_Set', N'EvaluationSets', 1), (N'UQ_EvaluationSets_Workspace_Name', N'EvaluationSets', 1),
             (N'IX_ExportLogs_Workspace_Export', N'ExportLogs', 0), (N'IX_AuditEvents_Workspace_Time', N'AuditEvents', 0),
             (N'UX_Workspaces_PersonalOwner', N'Workspaces', 1), (N'UX_WorkspaceMembers_OneOwner', N'WorkspaceMembers', 1),
             (N'UX_UserTokens_TokenHash', N'UserTokens', 1), (N'UX_UserExternalLogins_Provider_Key', N'UserExternalLogins', 1),
             (N'UX_UserExternalLogins_User_Provider', N'UserExternalLogins', 1)) r(Name, TableName, IsUnique);

INSERT #A51 (CheckName, Status, Detail)
SELECT N'composite foreign key (WorkspaceId, ...) trusted: ' + r.Name,
       CASE WHEN EXISTS (SELECT 1 FROM sys.foreign_keys f WHERE f.name = r.Name AND f.parent_object_id = OBJECT_ID(N'dbo.' + r.TableName)
                         AND f.is_disabled = 0 AND f.is_not_trusted = 0
                         AND (SELECT COUNT(*) FROM sys.foreign_key_columns k WHERE k.constraint_object_id = f.object_id) = 2) THEN 'PASS' ELSE 'FAIL' END, NULL
FROM (VALUES (N'FK_Applications_Jobs', N'Applications'), (N'FK_Applications_Cvs', N'Applications'),
             (N'FK_EvaluationRuns_PromptVersions', N'EvaluationRuns'), (N'FK_EvaluationRuns_EvaluationSets', N'EvaluationRuns'),
             (N'FK_EvaluationSets_Jobs', N'EvaluationSets'), (N'FK_Cvs_Candidates', N'Cvs')) r(Name, TableName);

-- 7. Active prompt: at most one per workspace
IF COL_LENGTH(N'dbo.PromptVersions', N'WorkspaceId') IS NOT NULL
    EXEC sys.sp_executesql N'
    INSERT #A51 (CheckName, Status, Detail)
    SELECT N''workspaces with more than one active prompt'', CASE WHEN COUNT(*) = 0 THEN ''PASS'' ELSE ''FAIL'' END, CAST(COUNT(*) AS nvarchar(20))
    FROM (SELECT WorkspaceId FROM dbo.PromptVersions WHERE IsActive = 1 GROUP BY WorkspaceId HAVING COUNT(*) > 1) x;';

-- 8. Cross-workspace links. The composite FKs make the first group impossible; the indirect links (second group) are
--    NOT enforced by the database in A51 (guide 6.4: scoped "qua" the parent) and must be prevented by A52/A53 code.
IF COL_LENGTH(N'dbo.Applications', N'WorkspaceId') IS NOT NULL AND COL_LENGTH(N'dbo.EvaluationRuns', N'WorkspaceId') IS NOT NULL
   AND COL_LENGTH(N'dbo.EvaluationSets', N'WorkspaceId') IS NOT NULL AND COL_LENGTH(N'dbo.Jobs', N'WorkspaceId') IS NOT NULL
BEGIN
    EXEC sys.sp_executesql N'
    INSERT #A51 (CheckName, Status, Detail)
    SELECT N''applications linked to a Job/CV of another workspace'', CASE WHEN COUNT(*) = 0 THEN ''PASS'' ELSE ''FAIL'' END, CAST(COUNT(*) AS nvarchar(20))
    FROM dbo.Applications a JOIN dbo.Jobs j ON j.JobId = a.JobId JOIN dbo.Cvs c ON c.CvId = a.CvId
    WHERE j.WorkspaceId <> a.WorkspaceId OR c.WorkspaceId <> a.WorkspaceId;

    INSERT #A51 (CheckName, Status, Detail)
    SELECT N''evaluations whose Run and Application are in different workspaces (not DB-enforced)'', CASE WHEN COUNT(*) = 0 THEN ''PASS'' ELSE ''FAIL'' END, CAST(COUNT(*) AS nvarchar(20))
    FROM dbo.Evaluations e JOIN dbo.EvaluationRuns r ON r.RunId = e.RunId JOIN dbo.Applications a ON a.ApplicationId = e.ApplicationId
    WHERE r.WorkspaceId <> a.WorkspaceId;

    INSERT #A51 (CheckName, Status, Detail)
    SELECT N''evaluation-set members / HR labels linking another workspace (not DB-enforced)'', CASE WHEN SUM(n) = 0 THEN ''PASS'' ELSE ''FAIL'' END, CAST(SUM(n) AS nvarchar(20))
    FROM (SELECT COUNT(*) AS n FROM dbo.EvaluationSetMembers m JOIN dbo.EvaluationSets s ON s.EvaluationSetId = m.EvaluationSetId JOIN dbo.Applications a ON a.ApplicationId = m.ApplicationId WHERE s.WorkspaceId <> a.WorkspaceId
          UNION ALL
          SELECT COUNT(*) FROM dbo.HrLabels l JOIN dbo.EvaluationSets s ON s.EvaluationSetId = l.EvaluationSetId JOIN dbo.Applications a ON a.ApplicationId = l.ApplicationId WHERE s.WorkspaceId <> a.WorkspaceId) x;

    INSERT #A51 (CheckName, Status, Detail)
    SELECT N''export logs whose Run or Job is in another workspace (not DB-enforced)'', CASE WHEN COUNT(*) = 0 THEN ''PASS'' ELSE ''FAIL'' END, CAST(COUNT(*) AS nvarchar(20))
    FROM dbo.ExportLogs x JOIN dbo.EvaluationRuns r ON r.RunId = x.RunId JOIN dbo.Jobs j ON j.JobId = x.JobId
    WHERE r.WorkspaceId <> x.WorkspaceId OR j.WorkspaceId <> x.WorkspaceId;';
END

-- 9. Historical append-only triggers are untouched (A35 ApplicationEvents, B43 AuditEvents, A31 ExportLogs)
INSERT #A51 (CheckName, Status, Detail)
SELECT N'trigger enabled: ' + r.Name,
       CASE WHEN EXISTS (SELECT 1 FROM sys.triggers t WHERE t.name = r.Name AND t.parent_id = OBJECT_ID(N'dbo.' + r.TableName) AND t.is_disabled = 0) THEN 'PASS' ELSE 'FAIL' END, NULL
FROM (VALUES (N'TR_ApplicationEvents_NoChange', N'ApplicationEvents'), (N'TR_AuditEvents_NoChange', N'AuditEvents'), (N'TR_ExportLogs_NoChange', N'ExportLogs')) r(Name, TableName);

SELECT Id, CheckName, Status, Detail FROM #A51 ORDER BY Id;

DECLARE @failed int = (SELECT COUNT(*) FROM #A51 WHERE Status = 'FAIL');
DROP TABLE #A51;
IF @failed > 0 THROW 52051, N'A51: at least one check FAILED. See the result set above.', 1;
