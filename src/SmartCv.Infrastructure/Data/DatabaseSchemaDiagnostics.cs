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
