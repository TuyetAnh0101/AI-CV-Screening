using Microsoft.EntityFrameworkCore;
using SmartCv.Api.Auth;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api;

/// <summary>
/// Read-only startup diagnostic. Never migrates or changes the database: it only reports, at Critical level,
/// migrations the running code expects but the configured database does not have yet (e.g. B43 AddAuditEvents,
/// whose absence makes every successful login fail with 503 because the login audit row cannot be inserted).
/// </summary>
public static class DatabaseStartupCheck
{
    public static async Task WarnIfSchemaOutOfDateAsync(this WebApplication app, CancellationToken ct = default)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SmartCv.Api.DatabaseStartupCheck");
        if (!db.Database.IsSqlServer()) return;
        try
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count == 0) return;
            var catalog = db.Database.GetDbConnection().Database;
            logger.LogCritical("Database {Database} is missing {Count} migration(s) required by this build: {Migrations}. " +
                "Features that write the new schema will fail (for 20261120043759_AddAuditEvents: every login returns 503; " +
                "for AddWorkspaces (A51) the business tables have no WorkspaceId yet, so queries and inserts fail). " +
                "Apply the reviewed script in database/ or `dotnet ef database update` after approval; this check never migrates.",
                catalog, pending.Count, string.Join(", ", pending));
        }
        catch (Exception error) when (error is not OperationCanceledException && AuthProblems.IsDatabaseFailure(error))
        {
            logger.LogWarning("Could not inspect database migrations at startup ({Type}, SQL {SqlNumber}).",
                error.GetType().Name, AuthProblems.SqlErrorNumber(error)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none");
        }
    }
}
