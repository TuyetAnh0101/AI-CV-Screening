using System.Data;
using Microsoft.EntityFrameworkCore;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Workspaces;

/// <summary>
/// Khóa một workspace trong suốt đợt upload, nhưng KHÔNG mở transaction SQL xuyên qua ghi file.
/// Session applock ngăn hai upload vượt quota; CvUploadService vẫn commit/cleanup từng file như A42.
/// </summary>
public static class WorkspaceCvUploadLock
{
    public static async Task<IAsyncDisposable> AcquireAsync(SmartCvDbContext db, int workspaceId, CancellationToken ct)
    {
        if (workspaceId <= 0) throw new ArgumentOutOfRangeException(nameof(workspaceId));
        if (!db.Database.IsSqlServer()) return new NoLock(); // InMemory fixture
        await db.Database.OpenConnectionAsync(ct);
        var resource = $"SCApp.Upload.Workspace.{workspaceId}";
        try
        {
            var outcome = await ExecuteAsync(db, "sp_getapplock", resource, acquire: true, ct);
            if (outcome < 0) throw new TimeoutException("Workspace đang có yêu cầu tải CV khác; vui lòng thử lại.");
            return new HeldLock(db, resource);
        }
        catch
        {
            await db.Database.CloseConnectionAsync();
            throw;
        }
    }

    private static async Task<int> ExecuteAsync(SmartCvDbContext db, string procedure, string resource,
        bool acquire, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = procedure;
        command.CommandType = CommandType.StoredProcedure;
        var p = command.CreateParameter(); p.ParameterName = "@Resource"; p.Value = resource; command.Parameters.Add(p);
        p = command.CreateParameter(); p.ParameterName = "@LockOwner"; p.Value = "Session"; command.Parameters.Add(p);
        if (acquire)
        {
            p = command.CreateParameter(); p.ParameterName = "@LockMode"; p.Value = "Exclusive"; command.Parameters.Add(p);
            p = command.CreateParameter(); p.ParameterName = "@LockTimeout"; p.Value = 10000; command.Parameters.Add(p);
        }
        p = command.CreateParameter(); p.ParameterName = "@Result";
        p.DbType = DbType.Int32; p.Direction = ParameterDirection.ReturnValue; command.Parameters.Add(p);
        await command.ExecuteNonQueryAsync(ct);
        return (int)p.Value!;
    }

    private sealed class NoLock : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HeldLock(SmartCvDbContext db, string resource) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await ExecuteAsync(db, "sp_releaseapplock", resource, acquire: false, CancellationToken.None); }
            finally { await db.Database.CloseConnectionAsync(); }
        }
    }
}

public sealed class CvQuotaExceededException(int limit, int used, int requested) : Exception("cv_quota_exceeded")
{
    public int Limit { get; } = limit;
    public int Used { get; } = used;
    public int Requested { get; } = requested;
}
