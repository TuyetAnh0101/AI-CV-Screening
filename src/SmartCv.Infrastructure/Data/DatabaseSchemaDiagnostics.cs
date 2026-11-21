using System.Data;
using Microsoft.EntityFrameworkCore;

namespace SmartCv.Infrastructure.Data;

public sealed record DatabaseReadiness(IReadOnlyList<string> PendingMigrations, IReadOnlyList<string> MissingObjects)
{
    public bool IsReady => PendingMigrations.Count == 0 && MissingObjects.Count == 0;
}

/// <summary>Read-only deployment verification. Never migrates, repairs or changes application data.</summary>
public static class DatabaseSchemaDiagnostics
{
    public static async Task<DatabaseReadiness> InspectAsync(SmartCvDbContext db, CancellationToken ct = default)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        var missing = new List<string>();
        if (!db.Database.IsSqlServer()) return new(pending, missing);
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        try
        {
            if (openedHere) await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 10;
            command.CommandText = """
                SELECT N'Applications.' + required.Name
                FROM (VALUES (N'Stage'), (N'RejectReason'), (N'StageUpdatedAt'), (N'StageVersion')) required(Name)
                WHERE NOT EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dbo.Applications') AND c.name = required.Name)
                UNION ALL
                SELECT N'ApplicationEvents' WHERE OBJECT_ID(N'dbo.ApplicationEvents', N'U') IS NULL
                UNION ALL
                SELECT N'AuditEvents' WHERE OBJECT_ID(N'dbo.AuditEvents', N'U') IS NULL
                UNION ALL
                SELECT N'ExportLogs' WHERE OBJECT_ID(N'dbo.ExportLogs', N'U') IS NULL
                UNION ALL
                SELECT required.Name
                FROM (VALUES (N'CK_Applications_Stage'), (N'CK_Applications_RejectReason'), (N'CK_Applications_RejectReason_Stage'),
                             (N'CK_ApplicationEvents_FromStage'), (N'CK_ApplicationEvents_ToStage'), (N'CK_ApplicationEvents_RejectReason'),
                             (N'CK_ApplicationEvents_RejectReason_Stage'), (N'CK_ApplicationEvents_Actor')) required(Name)
                WHERE NOT EXISTS (SELECT 1 FROM sys.check_constraints c WHERE c.name = required.Name AND c.is_disabled = 0 AND c.is_not_trusted = 0
                    AND c.parent_object_id IN (OBJECT_ID(N'dbo.Applications'), OBJECT_ID(N'dbo.ApplicationEvents')))
                UNION ALL
                SELECT required.Name
                FROM (VALUES (N'Applications', N'IX_Applications_Job_Stage'), (N'ApplicationEvents', N'IX_ApplicationEvents_Application_CreatedAt')) required(TableName, Name)
                WHERE NOT EXISTS (SELECT 1 FROM sys.indexes i WHERE i.name = required.Name AND i.object_id = OBJECT_ID(N'dbo.' + required.TableName) AND i.is_disabled = 0)
                UNION ALL
                SELECT N'FK_ApplicationEvents_Applications'
                WHERE NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ApplicationEvents_Applications'
                    AND parent_object_id = OBJECT_ID(N'dbo.ApplicationEvents') AND referenced_object_id = OBJECT_ID(N'dbo.Applications')
                    AND delete_referential_action = 0 AND is_disabled = 0 AND is_not_trusted = 0)
                UNION ALL
                SELECT N'A51 table ' + required.Name
                FROM (VALUES (N'Workspaces'), (N'WorkspaceMembers'), (N'UserTokens'), (N'UserExternalLogins')) required(Name)
                WHERE OBJECT_ID(N'dbo.' + required.Name, N'U') IS NULL
                UNION ALL
                SELECT N'A51 column ' + required.TableName + N'.' + required.Name
                FROM (VALUES (N'Jobs', N'WorkspaceId'), (N'Cvs', N'WorkspaceId'), (N'Candidates', N'WorkspaceId'), (N'Applications', N'WorkspaceId'),
                             (N'PromptVersions', N'WorkspaceId'), (N'EvaluationSets', N'WorkspaceId'), (N'EvaluationRuns', N'WorkspaceId'),
                             (N'ExportLogs', N'WorkspaceId'), (N'AuditEvents', N'WorkspaceId'),
                             (N'Users', N'EmailVerifiedAt'), (N'Users', N'SignupSource'), (N'Users', N'TermsAcceptedAt')) required(TableName, Name)
                WHERE NOT EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dbo.' + required.TableName) AND c.name = required.Name)
                UNION ALL
                SELECT N'A51 index ' + required.TableName + N'.' + required.Name
                FROM (VALUES (N'Jobs', N'UQ_Jobs_Workspace_Title'), (N'Cvs', N'IX_Cvs_Workspace_FileHash'),
                             (N'PromptVersions', N'UX_PromptVersions_Workspace_OneActive'), (N'AuditEvents', N'IX_AuditEvents_Workspace_Time')) required(TableName, Name)
                WHERE NOT EXISTS (SELECT 1 FROM sys.indexes i WHERE i.name = required.Name AND i.object_id = OBJECT_ID(N'dbo.' + required.TableName) AND i.is_disabled = 0)
                UNION ALL
                SELECT N'A51 foreign key ' + required.Name
                FROM (VALUES (N'FK_Applications_Jobs', N'Applications'), (N'FK_Applications_Cvs', N'Applications'),
                             (N'FK_EvaluationRuns_PromptVersions', N'EvaluationRuns'), (N'FK_EvaluationRuns_EvaluationSets', N'EvaluationRuns'),
                             (N'FK_EvaluationSets_Jobs', N'EvaluationSets'), (N'FK_Cvs_Candidates', N'Cvs')) required(Name, TableName)
                WHERE NOT EXISTS (SELECT 1 FROM sys.foreign_keys f WHERE f.name = required.Name AND f.parent_object_id = OBJECT_ID(N'dbo.' + required.TableName)
                    AND f.is_disabled = 0 AND f.is_not_trusted = 0
                    AND (SELECT COUNT(*) FROM sys.foreign_key_columns k WHERE k.constraint_object_id = f.object_id) = 2)
                UNION ALL
                SELECT N'A51 legacy global constraint ' + legacy.Name
                FROM (VALUES (N'UQ_Jobs_Title', N'Jobs'), (N'UQ_Cvs_FileHash', N'Cvs'), (N'UQ_PromptVersions_VersionName', N'PromptVersions'),
                             (N'UQ_EvaluationSets_Name', N'EvaluationSets'), (N'UX_PromptVersions_OneActive', N'PromptVersions')) legacy(Name, TableName)
                WHERE EXISTS (SELECT 1 FROM sys.indexes i WHERE i.name = legacy.Name AND i.object_id = OBJECT_ID(N'dbo.' + legacy.TableName))
                UNION ALL
                SELECT N'A51 internal workspace row' WHERE OBJECT_ID(N'dbo.Workspaces', N'U') IS NOT NULL
                    AND NOT EXISTS (SELECT 1 FROM dbo.Workspaces WHERE WorkspaceId = 1 AND Kind = 'Internal')
                UNION ALL
                SELECT N'TR_ApplicationEvents_NoChange'
                WHERE NOT EXISTS (SELECT 1 FROM sys.triggers t WHERE t.object_id = OBJECT_ID(N'dbo.TR_ApplicationEvents_NoChange')
                    AND t.parent_id = OBJECT_ID(N'dbo.ApplicationEvents') AND t.is_disabled = 0 AND t.is_instead_of_trigger = 1
                    AND EXISTS (SELECT 1 FROM sys.trigger_events e WHERE e.object_id = t.object_id AND e.type_desc = N'UPDATE')
                    AND EXISTS (SELECT 1 FROM sys.trigger_events e WHERE e.object_id = t.object_id AND e.type_desc = N'DELETE'))
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) missing.Add(reader.GetString(0));
            return new(pending, missing);
        }
        finally { if (openedHere) await connection.CloseAsync(); }
    }
}
