using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Prompts;

/// <summary>
/// Serializes A15 label changes and PromptTest creation for the same evaluation set.
/// The transaction protects the HR labels and run snapshot without adding schema.
/// </summary>
public static class EvaluationSetWriteScope
{
    public static async Task<IDbContextTransaction?> BeginAsync(
        SmartCvDbContext db, int setId, CancellationToken ct)
    {
        // The application uses SQL Server; InMemory endpoint tests have no relational transactions.
        if (!db.Database.IsSqlServer()) return null;

        var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            _ = await db.EvaluationSets
                .FromSqlInterpolated($"SELECT * FROM dbo.EvaluationSets WITH (UPDLOCK, HOLDLOCK) WHERE EvaluationSetId = {setId}")
                .AsNoTracking().Select(s => s.EvaluationSetId).FirstOrDefaultAsync(ct);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }
}
