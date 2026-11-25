using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Workspaces;

namespace SmartCv.Tests;

public class A53JournalSqlTests
{
    internal const string Current = "20261121061232_ScopeCandidateDataRequests";
    /// <summary>Migrations appended after A53, in order (A54 ledger, email outbox, Gemini logs, ranking PDF export).</summary>
    internal static readonly string[] Later =
    [
        "20261122082301_A54AiUsageLedgerAndPromptSnapshot", "20261122092023_AddEmailOutbox",
        "20261123210000_AddGeminiInvocationLogs", "20261124223000_AllowRankingPdfExport"
    ];

    [A51SqlFact]
    public async Task Migration_preserves_live_and_deleted_candidate_journal_ownership_without_rewriting_accounts()
    {
        await using var database = await A51Database.CreateAsync(A51Database.After);
        await database.ExecAsync("""
            INSERT INTO Workspaces (Name, Kind) VALUES (N'Synthetic B', 'Personal');
            INSERT INTO Candidates (WorkspaceId, FullName) VALUES (2, N'Synthetic candidate');
            INSERT INTO CandidateDataRequests (CandidateId, RequestType, HandledBy)
              SELECT CandidateId, 'Export', N'Synthetic' FROM Candidates WHERE WorkspaceId = 2;
            INSERT INTO Cvs (WorkspaceId, CvId, OriginalFileName, StoredPath, ContentType, FileSizeBytes, FileHash)
              VALUES (2, 'aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb', N'synthetic.pdf', N'synthetic', N'application/pdf', 1, REPLICATE('a',64));
            INSERT INTO CandidateDataRequests (CandidateId, RequestType, HandledBy, CvIds)
              VALUES (901, 'Erase', N'Synthetic deleted candidate', 'aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb');
            """);
        var usersBefore = await database.IntAsync("SELECT COUNT(*) FROM Users");
        await database.MigrateAsync(Current);
        Assert.Equal(2, await database.IntAsync("SELECT COUNT(*) FROM CandidateDataRequests WHERE WorkspaceId = 2"));
        Assert.Equal(usersBefore, await database.IntAsync("SELECT COUNT(*) FROM Users"));
        using (WorkspaceScope.Begin(2))
        {
            await using var db = database.Context();
            Assert.Equal(2, await db.CandidateDataRequests.CountAsync());
            db.CandidateDataRequests.Add(new() { CandidateId = 902, RequestType = "Erase", HandledBy = "Synthetic zero-CV candidate" });
            await db.SaveChangesAsync();
            Assert.Equal(2, (await db.CandidateDataRequests.SingleAsync(r => r.CandidateId == 902)).WorkspaceId);
        }
        using (WorkspaceScope.Begin(1)) { await using var db = database.Context(); Assert.Empty(await db.CandidateDataRequests.ToListAsync()); }
    }

    [A51SqlFact]
    public async Task Migration_refuses_ambiguous_multiworkspace_orphans_and_rolls_back()
    {
        await using var database = await A51Database.CreateAsync(A51Database.After);
        await database.ExecAsync("""
            INSERT INTO Workspaces (Name, Kind) VALUES (N'Synthetic B', 'Personal');
            INSERT INTO CandidateDataRequests (CandidateId, RequestType, HandledBy) VALUES (999, 'Erase', N'Synthetic');
            """);
        var error = await Assert.ThrowsAsync<SqlException>(() => database.MigrateAsync(Current));
        Assert.Equal(51053, error.Number);
        Assert.Equal(0, await database.IntAsync("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('CandidateDataRequests') AND name = 'WorkspaceId'"));
        Assert.Equal(1, await database.IntAsync("SELECT COUNT(*) FROM CandidateDataRequests"));
    }

    [A51SqlFact]
    public async Task Migration_maps_internal_only_legacy_orphans_and_can_be_reapplied_idempotently()
    {
        await using var database = await A51Database.CreateAsync(A51Database.After);
        await database.ExecAsync("INSERT INTO CandidateDataRequests (CandidateId, RequestType, HandledBy) VALUES (999, 'Erase', N'Synthetic legacy');");
        await database.MigrateAsync(Current); await database.MigrateAsync(Current);
        Assert.Equal(1, await database.IntAsync("SELECT WorkspaceId FROM CandidateDataRequests WHERE CandidateId = 999"));
    }
}
