using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SmartCv.Api.Auth;
using SmartCv.Core.Entities;
using SmartCv.Core.Workspaces;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Workspaces;

namespace SmartCv.Tests;

/// <summary>
/// A52 trên SQL Server thật (database tạm SmartCv_A51_Test_{guid} đã áp migration A51, tự tạo và tự xóa): bản dịch SQL của query filter
/// (kể cả filter qua navigation), khóa ngoại ghép khi đóng dấu đồ thị, concurrency token chặn Attach giả, truy vấn phiên một lần.
/// </summary>
public class A52SqlTests
{
    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Reads;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { Interlocked.Increment(ref Reads); return base.ReaderExecutingAsync(command, eventData, result, ct); }
    }

    [A51SqlFact]
    public async Task Sql_filters_isolate_workspaces_including_navigation_children_and_fail_closed()
    {
        await using var database = await A51Database.CreateAsync(A51Database.Latest);
        var options = await A52Data.SeedAsync(database.Options());   // đồ thị đầy đủ, đóng dấu qua SaveChanges, FK ghép + AK thật

        await using (var none = new SmartCvDbContext(options))
            foreach (var (name, count) in A52QueryFilterTests.Counters)
                Assert.True(0 == await count(none), $"{name}: không có workspace phải trả 0 dòng (kể cả 5 job nền của workspace 1)");

        foreach (var (mine, other) in new[] { (1, 2), (2, 1) })
        {
            using var scope = WorkspaceScope.Begin(mine);
            await using var db = new SmartCvDbContext(options);
            Assert.Equal([$"A52 job {mine}"], await db.Jobs.Where(j => j.Title.StartsWith("A52")).Select(j => j.Title).ToListAsync());
            Assert.Equal(mine == 1 ? 5 + 1 : 1, await db.Jobs.CountAsync());   // workspace 1 còn 5 job nền từ migration SeedJobs
            Assert.Equal(1, await db.Cvs.CountAsync()); Assert.Equal(1, await db.Applications.CountAsync());
            Assert.Equal(1, await db.Evaluations.CountAsync()); Assert.Equal(1, await db.EvaluationSkills.CountAsync());
            Assert.Equal(1, await db.ApplicationEvents.CountAsync()); Assert.Equal(1, await db.ApplicationFlags.CountAsync());
            Assert.Equal(1, await db.ApplicationFlagEvents.CountAsync()); Assert.Equal(1, await db.HrLabels.CountAsync());
            Assert.Equal(1, await db.EvaluationSetMembers.CountAsync()); Assert.Equal(mine == 1 ? 52 + 1 : 1, await db.JobSkillAliases.CountAsync());   // + 52 từ khóa nền (SeedJobs)
            Assert.Equal(1, await db.CandidateDataRequests.CountAsync());
            var app = Assert.Single(await db.Applications.Include(a => a.Job).Include(a => a.Cv).ThenInclude(c => c.Candidate).Include(a => a.Evaluations).Include(a => a.Events).ToListAsync());
            Assert.Equal($"A52 job {mine}", app.Job.Title); Assert.Equal($"A52 candidate {mine}", app.Cv.Candidate!.FullName);
            Assert.Equal([$"A52 job {mine}"], await (from a in db.Applications join j in db.Jobs on a.JobId equals j.JobId select j.Title).ToListAsync());
            Assert.Empty(await db.Jobs.Where(j => j.Title == $"A52 job {other}").ToListAsync());
        }

        using (WorkspaceScope.BeginSystem("test: kiểm tra system scope trên SQL Server"))
        {
            await using var db = new SmartCvDbContext(options);
            Assert.Equal(2, await db.Applications.CountAsync());
            Assert.Equal(2, await db.Jobs.CountAsync(j => j.Title.StartsWith("A52")));
        }
    }

    [A51SqlFact]
    public async Task Sql_save_guard_stamps_graphs_blocks_forged_updates_and_keeps_cross_workspace_foreign_keys()
    {
        await using var database = await A51Database.CreateAsync(A51Database.Latest);
        var options = await A52Data.SeedAsync(database.Options());
        int foreignJob;
        using (WorkspaceScope.Begin(2)) { await using var db = new SmartCvDbContext(options); foreignJob = (await db.Jobs.SingleAsync(j => j.Title == "A52 job 2")).JobId; }

        using var scope = WorkspaceScope.Begin(1);
        // Đóng dấu cả đồ thị trên SQL (khóa ngoại ghép (WorkspaceId, CvId/JobId) phải thỏa).
        await using (var db = new SmartCvDbContext(options))
        {
            var cv = new Cv { CvId = Guid.NewGuid(), Candidate = new Candidate { FullName = "A52 sql graph" }, OriginalFileName = "g.pdf", StoredPath = "g", ContentType = "application/pdf", FileSizeBytes = 1, FileHash = new string('7', 64) };
            var app = new Application { Cv = cv, Job = new Job { Title = "A52 sql graph job" } };
            db.Add(app); await db.SaveChangesAsync();
            Assert.All(new[] { app.WorkspaceId, cv.WorkspaceId, app.Job.WorkspaceId, cv.Candidate!.WorkspaceId }, w => Assert.Equal(1, w));
        }
        // Ghi sai workspace / không workspace bị chặn trước khi gửi SQL.
        await using (var db = new SmartCvDbContext(options)) { db.Jobs.Add(new Job { Title = "A52 sql foreign", WorkspaceId = 2 }); await Assert.ThrowsAsync<WorkspaceViolationException>(() => db.SaveChangesAsync()); }
        await using (var db = new SmartCvDbContext(options)) { var run = await db.EvaluationRuns.SingleAsync(); run.WorkspaceId = 2; await Assert.ThrowsAsync<WorkspaceViolationException>(() => db.SaveChangesAsync()); }
        // Attach/Update giả khóa của workspace khác: WHERE ... AND WorkspaceId = @gốc => 0 dòng.
        await using (var db = new SmartCvDbContext(options)) { db.Jobs.Update(new Job { JobId = foreignJob, WorkspaceId = 1, Title = "A52 hijack" }); await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync()); }
        await using (var db = new SmartCvDbContext(options)) { db.Jobs.Remove(new Job { JobId = foreignJob, WorkspaceId = 1, Title = "x" }); await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync()); }
        Assert.Equal("A52 job 2", await database.ScalarAsync<string>($"SELECT Title FROM Jobs WHERE JobId = {foreignJob}"));

        // ExecuteUpdate/ExecuteDelete có áp query filter (gốc và con qua navigation).
        await using (var db = new SmartCvDbContext(options))
        {
            Assert.Equal(0, await db.Jobs.Where(j => j.Title == "A52 job 2").ExecuteDeleteAsync());
            Assert.Equal(0, await db.Jobs.Where(j => j.JobId == foreignJob).ExecuteUpdateAsync(s => s.SetProperty(j => j.Description, "x")));
            Assert.Equal(1, await db.HrLabels.ExecuteUpdateAsync(s => s.SetProperty(h => h.Note, "chỉ workspace 1")));
        }
        Assert.Equal(1, await database.IntAsync("SELECT COUNT(*) FROM HrLabels WHERE Note = N'chỉ workspace 1'"));

        // ExportLog (bảng chỉ-thêm có trigger) nhận WorkspaceId khi thêm và chỉ hiện ở workspace của nó.
        await using (var db = new SmartCvDbContext(options))
        {
            var runId = (await db.EvaluationRuns.SingleAsync()).RunId; var jobId = (await db.Jobs.FirstAsync(j => j.Title == "A52 job 1")).JobId;
            db.ExportLogs.Add(new ExportLog
            {
                AuditEvent = new AuditEvent { ActionCode = "export.ranking", Category = "export", ResourceType = "run", Outcome = "Succeeded", RequestId = "a52", SafeSummary = "A52", WorkspaceId = 1 },
                RunId = runId, JobId = jobId, Stage = "all", Fit = "all", RowCount = 1
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await database.IntAsync("SELECT COUNT(*) FROM ExportLogs WHERE WorkspaceId = 1"));
        await using (var db = new SmartCvDbContext(options)) Assert.Equal(1, await db.ExportLogs.CountAsync());
        using (WorkspaceScope.Begin(2)) { await using var db = new SmartCvDbContext(options); Assert.Equal(0, await db.ExportLogs.CountAsync()); }
    }

    [A51SqlFact]
    public async Task Sql_session_state_reads_account_and_membership_in_one_query_and_reports_revocation()
    {
        await using var database = await A51Database.CreateAsync(A51Database.Latest);
        await database.ExecAsync("""
            INSERT INTO Users (Email, FullName, PasswordHash, Role, IsActive, MustChangePassword, SecurityStamp, SignupSource)
            VALUES (N'a52-hr@example.invalid', N'HR A52', 'h', N'HR', 1, 0, 'stamp-hr', 'Admin'),
                   (N'a52-owner@example.invalid', N'Owner A52', 'h', N'HR', 1, 0, 'stamp-owner', 'Local');
            INSERT INTO Workspaces (Name, Kind, OwnerUserId) VALUES (N'Cá nhân A52', 'Personal', 2);
            INSERT INTO WorkspaceMembers (WorkspaceId, UserId, Role) VALUES (1, 1, 'Member'), (2, 2, 'Owner');
            """);
        var counter = new CommandCounter();
        await using var db = new SmartCvDbContext(database.Options(counter));
        var store = new DbSessionStateStore(db);

        var member = (await store.GetAsync(1, 1, CancellationToken.None))!;
        Assert.Equal(1, counter.Reads);   // tài khoản + membership trong MỘT truy vấn
        Assert.Equal(new WorkspaceSessionInfo(1, "SCApp nội bộ", "Internal", "Member"), member.Workspace);
        Assert.True(member.WorkspaceActive); Assert.Equal("HR", member.Role); Assert.Equal("stamp-hr", member.SecurityStamp);

        Assert.Null((await store.GetAsync(1, 2, CancellationToken.None))!.Workspace);                       // không phải thành viên workspace 2
        Assert.Equal("Owner", (await store.GetAsync(2, 2, CancellationToken.None))!.Workspace!.Role);      // vai trò lấy từ database
        Assert.Null((await store.GetAsync(1, null, CancellationToken.None))!.Workspace);                    // token cũ: không hỏi workspace
        Assert.Null(await store.GetAsync(999, 1, CancellationToken.None));
        Assert.Null((await store.GetAsync(1, 999, CancellationToken.None))!.Workspace);

        await database.ExecAsync("UPDATE Workspaces SET IsActive = 0 WHERE WorkspaceId = 2");
        var inactive = (await store.GetAsync(2, 2, CancellationToken.None))!;
        Assert.False(inactive.WorkspaceActive);
        Assert.Equal(SessionRulesRevoked, SmartCv.Core.Auth.SessionRules.CheckWorkspace(inactive, 2));
        await database.ExecAsync("DELETE FROM WorkspaceMembers WHERE UserId = 1");
        Assert.Equal(SessionRulesRevoked, SmartCv.Core.Auth.SessionRules.CheckWorkspace((await store.GetAsync(1, 1, CancellationToken.None))!, 1));
        Assert.Equal(1, await database.IntAsync("SELECT COUNT(*) FROM Users WHERE UserId = 1 AND Role = N'HR' AND SecurityStamp = 'stamp-hr'"));   // tài khoản không bị đổi
    }

    private const string SessionRulesRevoked = SmartCv.Core.Auth.SessionRules.WorkspaceRevoked;
}
