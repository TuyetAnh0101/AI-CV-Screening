using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Candidates;

/// <summary>
/// A24 writes are serialized before reading eligibility/duplicates. SQL Server uses a transaction-owned
/// database lock across API processes, then a serializable transaction. No write is retried.
/// The process lock only supplies deterministic serialization for the InMemory test provider.
/// Acquire the existing A39 shared privacy lease BEFORE this scope (HTTP middleware).
/// </summary>
public sealed class CandidateWriteScope : IAsyncDisposable
{
    private static readonly SemaphoreSlim InMemoryWrites = new(1, 1);
    private readonly IDbContextTransaction? transaction;
    private readonly bool inMemory;

    private CandidateWriteScope(IDbContextTransaction? transaction, bool inMemory) =>
        (this.transaction, this.inMemory) = (transaction, inMemory);

    public static async Task<CandidateWriteScope> BeginAsync(SmartCvDbContext db, CancellationToken ct)
    {
        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            if (!await InMemoryWrites.WaitAsync(TimeSpan.FromSeconds(5), ct))
                throw new TimeoutException("Candidate write lock unavailable.");
            return new(null, true);
        }
        if (!db.Database.IsSqlServer())
            throw new NotSupportedException("Candidate writes require SQL Server.");

        var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=N'SmartCv:A24:candidate-writes', " +
                "@LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=5000; " +
                "IF @r < 0 THROW 51024, 'Candidate write lock unavailable.', 1;", ct);
            return new(tx, false);
        }
        catch (Exception ex)
        {
            await tx.DisposeAsync();
            if (ct.IsCancellationRequested && ex is SqlException)
                throw new OperationCanceledException("Candidate write lock cancelled.", ex, ct);
            throw;
        }
    }

    public Task CommitAsync(CancellationToken ct) => transaction?.CommitAsync(ct) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (transaction is not null) await transaction.DisposeAsync();
        if (inMemory) InMemoryWrites.Release();
    }
}
