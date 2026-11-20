using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Auth;

/// <summary>Transaction-owned account lock across API instances. Local serialization also supports InMemory tests.</summary>
public sealed class AccountWriteCoordinator : IDisposable
{
    private readonly SemaphoreSlim serial = new(1);
    public const string LockSql = """
        DECLARE @result int;
        EXEC @result = sys.sp_getapplock @Resource=N'SCApp.A30.AccountSecurity',
            @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000;
        IF @result < 0 THROW 51030, N'Account security lock unavailable.', 1;
        """;

    public async Task<WriteScope> BeginAsync(SmartCvDbContext db, CancellationToken ct)
    {
        await serial.WaitAsync(ct);
        IDbContextTransaction? transaction = null;
        try
        {
            if (db.Database.IsSqlServer())
            {
                transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                await db.Database.ExecuteSqlRawAsync(LockSql, ct);
            }
            else if (db.Database.ProviderName != "Microsoft.EntityFrameworkCore.InMemory")
                throw new AccountFailure(503, "account_unavailable", "Chức năng tài khoản yêu cầu SQL Server.");
            return new WriteScope(transaction, serial);
        }
        catch
        {
            try { if (transaction is not null) await transaction.DisposeAsync(); }
            finally { serial.Release(); }
            throw;
        }
    }
    public void Dispose() => serial.Dispose();

    public sealed class WriteScope(IDbContextTransaction? transaction, SemaphoreSlim serial) : IAsyncDisposable
    {
        private bool disposed;
        public Task CommitAsync(CancellationToken ct) => transaction?.CommitAsync(ct) ?? Task.CompletedTask;
        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            try { if (transaction is not null) await transaction.DisposeAsync(); }
            finally { serial.Release(); }
        }
    }
}

internal sealed class AccountFailure(int status, string code, string message, Dictionary<string, string[]>? errors = null) : Exception(message)
{
    internal int Status { get; } = status;
    internal string Code { get; } = code;
    internal Dictionary<string, string[]>? Errors { get; } = errors;
    internal static AccountFailure Invalid(Dictionary<string, string[]> errors) => new(400, "validation_failed", "Dữ liệu chưa hợp lệ.", errors);
    internal static AccountFailure Conflict() => new(409, "account_conflict", "Tài khoản vừa thay đổi. Hãy tải lại trước khi thao tác.");
}