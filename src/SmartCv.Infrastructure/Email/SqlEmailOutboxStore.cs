using System.Data;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Email;

public sealed class SqlEmailOutboxStore(SmartCvDbContext db) : IEmailOutboxStore
{
    public async Task<IReadOnlyList<long>> ClaimDueAsync(Guid claimId, int batchSize, DateTime nowUtc,
        DateTime leaseUntilUtc, CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlServer()) throw new NotSupportedException("EmailWorker yêu cầu SQL Server cho atomic claim.");
        if (batchSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));
        const string sql = """
            ;WITH due AS (
                SELECT TOP (@batch) EmailOutboxId
                FROM dbo.EmailOutbox WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
                WHERE [Status] = 'Pending' AND NextAttemptAt <= @now
                  AND (LeaseUntilUtc IS NULL OR LeaseUntilUtc <= @now)
                ORDER BY NextAttemptAt, EmailOutboxId
            )
            UPDATE o SET ClaimId = @claim, LeaseUntilUtc = @lease
            OUTPUT inserted.EmailOutboxId
            FROM dbo.EmailOutbox AS o INNER JOIN due AS d ON d.EmailOutboxId = o.EmailOutboxId;
            """;
        // DbConnection của DbContext này chỉ được dùng bởi một worker scope tại một thời điểm.
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            command.CommandType = CommandType.Text;
            void Add(string name, DbType type, object value)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.DbType = type;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }
            Add("@batch", DbType.Int32, batchSize);
            Add("@now", DbType.DateTime2, nowUtc);
            Add("@claim", DbType.Guid, claimId);
            Add("@lease", DbType.DateTime2, leaseUntilUtc);
            var ids = new List<long>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt64(0));
            return ids;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    public Task<EmailOutboxItem?> ReadClaimedAsync(long id, Guid claimId, CancellationToken cancellationToken) =>
        db.EmailOutbox.AsNoTracking().SingleOrDefaultAsync(e => e.EmailOutboxId == id
            && e.ClaimId == claimId && e.Status == "Pending", cancellationToken);

    public async Task<bool> MarkSentAsync(long id, Guid claimId, DateTime nowUtc, CancellationToken cancellationToken) =>
        await db.EmailOutbox.Where(e => e.EmailOutboxId == id && e.ClaimId == claimId && e.Status == "Pending")
            .ExecuteUpdateAsync(x => x.SetProperty(e => e.Status, "Sent")
                .SetProperty(e => e.SentAt, nowUtc)
                .SetProperty(e => e.ProtectedPayload, (string?)null)
                .SetProperty(e => e.ClaimId, (Guid?)null)
                .SetProperty(e => e.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(e => e.LastError, (string?)null), cancellationToken) == 1;

    public async Task<bool> MarkFailureAsync(long id, Guid claimId, byte attempts, DateTime nextUtc,
        bool terminal, string errorCode, CancellationToken cancellationToken)
    {
        var q = db.EmailOutbox.Where(e => e.EmailOutboxId == id && e.ClaimId == claimId && e.Status == "Pending");
        if (terminal)
        {
            return await q.ExecuteUpdateAsync(x => x.SetProperty(e => e.Status, "Failed")
                .SetProperty(e => e.Attempts, attempts)
                .SetProperty(e => e.NextAttemptAt, nextUtc)
                .SetProperty(e => e.ProtectedPayload, (string?)null)
                .SetProperty(e => e.ClaimId, (Guid?)null)
                .SetProperty(e => e.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(e => e.LastError, errorCode), cancellationToken) == 1;
        }
        return await q.ExecuteUpdateAsync(x => x.SetProperty(e => e.Status, "Pending")
            .SetProperty(e => e.Attempts, attempts)
            .SetProperty(e => e.NextAttemptAt, nextUtc)
            .SetProperty(e => e.ClaimId, (Guid?)null)
            .SetProperty(e => e.LeaseUntilUtc, (DateTime?)null)
            .SetProperty(e => e.LastError, errorCode), cancellationToken) == 1;
    }
}
