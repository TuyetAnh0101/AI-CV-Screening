using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Privacy;

public sealed class CvPrivacyBusyException : Exception;
/// <summary>Database-scoped, cross-process privacy lease. Acquire before other transaction locks.</summary>
public sealed class CvPrivacyScope : IAsyncDisposable
{
    private readonly SmartCvDbContext db;
    private CvPrivacyScope(SmartCvDbContext db) => this.db = db;
    public static async Task<CvPrivacyScope?> BeginAsync(SmartCvDbContext db, bool exclusive, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer())
            return null;
        await db.Database.OpenConnectionAsync(ct);
        var outcomeKnown = false;
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=N'SmartCv:A39:privacy', @LockMode=@mode, @LockOwner=N'Session', @LockTimeout=5000; SELECT @r;";
            var mode = command.CreateParameter();
            mode.ParameterName = "@mode";
            mode.Value = exclusive ? "Exclusive" : "Shared";
            command.Parameters.Add(mode);
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            outcomeKnown = true;
            if (result < 0)
                throw new CvPrivacyBusyException();
            return new(db);
        }
        catch (Exception ex)
        {
            // If cancellation/network failure hid the acquisition result, do not return a possibly
            // locked physical session to the pool. Other in-use connections are not interrupted.
            if (!outcomeKnown && db.Database.GetDbConnection() is SqlConnection sql)
                SqlConnection.ClearPool(sql);
            await db.Database.CloseConnectionAsync();
            // SqlClient can surface command cancellation as SqlException instead of a canceled Task.
            // Preserve CancellationToken semantics without relaxing the caller's regression assertion.
            if (ct.IsCancellationRequested && ex is SqlException)
                throw new OperationCanceledException("Đã hủy chờ khóa bảo vệ dữ liệu CV.", ex, ct);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "EXEC sys.sp_releaseapplock @Resource=N'SmartCv:A39:privacy', @LockOwner=N'Session';";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch
        {
            if (db.Database.GetDbConnection() is SqlConnection sql)
                SqlConnection.ClearPool(sql);
            throw;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
