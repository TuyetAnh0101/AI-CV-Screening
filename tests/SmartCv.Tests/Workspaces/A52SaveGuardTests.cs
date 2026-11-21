using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;
using SmartCv.Core.Workspaces;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Workspaces;

namespace SmartCv.Tests;

/// <summary>A52: lớp ghi của SmartCvDbContext. Mọi đường SaveChanges (đồng bộ/bất đồng bộ, có/không tham số) đều qua cùng một guard.</summary>
public class A52SaveGuardTests
{
    private static Task<int> Save(SmartCvDbContext db, string method) => method switch
    {
        "SaveChanges()" => Task.FromResult(db.SaveChanges()),
        "SaveChanges(bool)" => Task.FromResult(db.SaveChanges(true)),
        "SaveChangesAsync()" => db.SaveChangesAsync(),
        _ => db.SaveChangesAsync(true, CancellationToken.None)
    };

    public static TheoryData<string> AllSaveMethods => ["SaveChanges()", "SaveChanges(bool)", "SaveChangesAsync()", "SaveChangesAsync(bool,CancellationToken)"];

    private static async Task<int> CountAllAsync(DbContextOptions<SmartCvDbContext> options)
    {
        using var system = WorkspaceScope.BeginSystem("test: đếm dữ liệu");
        await using var db = new SmartCvDbContext(options);
        return await db.Jobs.CountAsync(j => j.Title.StartsWith("A52 guard"));
    }

    [Theory, MemberData(nameof(AllSaveMethods))]
    public async Task Added_entity_without_workspace_id_is_stamped_with_the_current_workspace(string method)
    {
        var options = await A52Data.SeedAsync();
        using (WorkspaceScope.Begin(2))
        {
            await using var db = new SmartCvDbContext(options);
            var job = new Job { Title = "A52 guard stamped", CreatedAt = DateTime.UtcNow };
            Assert.Equal(WorkspaceKinds.UnassignedWorkspaceId, job.WorkspaceId);
            db.Jobs.Add(job);
            await Save(db, method);
            Assert.Equal(2, job.WorkspaceId);
        }
        using (WorkspaceScope.Begin(2)) { await using var db = new SmartCvDbContext(options); Assert.Equal(2, (await db.Jobs.SingleAsync(j => j.Title == "A52 guard stamped")).WorkspaceId); }
        using (WorkspaceScope.Begin(1)) { await using var db = new SmartCvDbContext(options); Assert.Empty(await db.Jobs.Where(j => j.Title == "A52 guard stamped").ToListAsync()); }
    }

    [Fact]
    public async Task A_whole_graph_is_stamped_and_keeps_its_relationships()
    {
        var options = A52Data.InMemory();
        await using (var db = new SmartCvDbContext(options)) await db.EnsureInternalWorkspaceAsync();
        using (WorkspaceScope.Begin(1))
        {
            await using var db = new SmartCvDbContext(options);
            var cv = new Cv { CvId = Guid.NewGuid(), Candidate = new Candidate { FullName = "A52 graph" }, OriginalFileName = "g.pdf", StoredPath = "g", ContentType = "application/pdf", FileSizeBytes = 1, FileHash = new string('g', 64) };
            var app = new Application { Cv = cv, Job = new Job { Title = "A52 guard graph" } };
            db.Add(app);
            await db.SaveChangesAsync();
            Assert.All(new int[] { cv.WorkspaceId, app.WorkspaceId, app.Job.WorkspaceId, cv.Candidate!.WorkspaceId }, w => Assert.Equal(1, w));
            Assert.Equal(app.JobId, app.Job.JobId);
        }
    }

    [Theory, MemberData(nameof(AllSaveMethods))]
    public async Task Added_entity_for_another_workspace_is_rejected_without_saving_anything(string method)
    {
        var options = await A52Data.SeedAsync();
        using (WorkspaceScope.Begin(1))
        {
            await using var db = new SmartCvDbContext(options);
            db.Jobs.Add(new Job { Title = "A52 guard valid", WorkspaceId = 1 });
            db.Jobs.Add(new Job { Title = "A52 guard foreign", WorkspaceId = 2 });
            var error = await Assert.ThrowsAsync<WorkspaceViolationException>(() => Save(db, method));
            Assert.Equal(("Job", "Added"), (error.EntityType, error.Operation));
            // Thông điệp không lộ mã workspace, mã bản ghi hay tên.
            Assert.DoesNotContain("foreign", error.Message); Assert.DoesNotMatch(@"\d", error.Message);
        }
        Assert.Equal(0, await CountAllAsync(options));   // rollback: cả bản ghi hợp lệ cùng lô cũng không được ghi
    }

    [Theory, MemberData(nameof(AllSaveMethods))]
    public async Task Without_a_workspace_nothing_business_can_be_written_but_global_data_still_can(string method)
    {
        var options = await A52Data.SeedAsync();
        await using var db = new SmartCvDbContext(options);
        db.Jobs.Add(new Job { Title = "A52 guard none" });
        await Assert.ThrowsAsync<WorkspaceViolationException>(() => Save(db, method));
        db.ChangeTracker.Clear();
        db.Jobs.Add(new Job { Title = "A52 guard none", WorkspaceId = 1 });   // đặt sẵn WorkspaceId cũng không vượt qua được
        await Assert.ThrowsAsync<WorkspaceViolationException>(() => Save(db, method));
        db.ChangeTracker.Clear();
        // Users/Workspaces/AuditEvents là dữ liệu toàn cục: đăng nhập vẫn ghi được audit trước khi có workspace.
        db.Users.Add(new User { Email = Guid.NewGuid().ToString("N") + "@example.invalid", FullName = "A52", Role = "HR", PasswordHash = "x" });
        Assert.Equal(1, await Save(db, method));
    }

    [Fact]
    public async Task System_scope_may_write_explicit_workspaces_but_never_stamps_or_moves_entities()
    {
        var options = await A52Data.SeedAsync();
        using (WorkspaceScope.BeginSystem("test: ghi hệ thống"))
        {
            await using var db = new SmartCvDbContext(options);
            db.Jobs.Add(new Job { Title = "A52 guard system-0" });   // không có workspace hiện hành để đóng dấu
            await Assert.ThrowsAsync<WorkspaceViolationException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();
            db.Jobs.Add(new Job { Title = "A52 guard system-2", WorkspaceId = 2 });
            await db.SaveChangesAsync();
            var job = await db.Jobs.SingleAsync(j => j.Title == "A52 guard system-2");
            job.WorkspaceId = 1;   // system scope cũng không được chuyển bản ghi sang workspace khác
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
    }

    [Theory, MemberData(nameof(AllSaveMethods))]
    public async Task Moving_an_entity_to_another_workspace_is_rejected_by_the_workspace_guard(string method)
    {
        var options = await A52Data.SeedAsync();
        using var scope = WorkspaceScope.Begin(1);
        await using var db = new SmartCvDbContext(options);
        var run = await db.EvaluationRuns.SingleAsync();   // WorkspaceId không thuộc khóa nên EF cho phép sửa; guard của A52 phải chặn
        run.WorkspaceId = 2;
        var error = await Assert.ThrowsAsync<WorkspaceViolationException>(() => Save(db, method));
        Assert.Equal(("EvaluationRun", "Modified"), (error.EntityType, error.Operation));
        db.ChangeTracker.Clear();
        Assert.Equal(1, (await db.EvaluationRuns.SingleAsync()).WorkspaceId);
    }

    [Fact]
    public async Task Entities_whose_WorkspaceId_is_part_of_a_key_cannot_be_moved_either()
    {
        var options = await A52Data.SeedAsync();
        using var scope = WorkspaceScope.Begin(1);
        await using var db = new SmartCvDbContext(options);
        var job = await db.Jobs.SingleAsync(j => j.Title == "A52 job 1");
        job.WorkspaceId = 2;   // EF chặn sửa thuộc tính khóa (alternate key); dù lỗi do EF hay guard, bản ghi không được chuyển
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(1, (await db.Jobs.SingleAsync(j => j.Title == "A52 job 1")).WorkspaceId);
    }

    [Fact]
    public async Task Attached_entities_of_another_workspace_cannot_be_updated_or_deleted_by_any_forged_shape()
    {
        var options = await A52Data.SeedAsync();
        int foreignId;
        using (WorkspaceScope.Begin(2)) { await using var db = new SmartCvDbContext(options); foreignId = (await db.Jobs.SingleAsync(j => j.Title == "A52 job 2")).JobId; }

        using var scope = WorkspaceScope.Begin(1);
        // 1. Khai đúng workspace của bản ghi (2) rồi Update: guard chặn vì không phải workspace hiện hành.
        await using (var db = new SmartCvDbContext(options))
        {
            db.Jobs.Update(new Job { JobId = foreignId, WorkspaceId = 2, Title = "A52 hijack" });
            await Assert.ThrowsAsync<WorkspaceViolationException>(() => db.SaveChangesAsync());
        }
        // 2. Khai workspace hiện hành (1) nhưng khóa của bản ghi workspace 2: guard không biết, nhưng WorkspaceId là concurrency token
        //    nên UPDATE ... WHERE JobId = @id AND WorkspaceId = 1 ghi 0 dòng.
        await using (var db = new SmartCvDbContext(options))
        {
            db.Jobs.Update(new Job { JobId = foreignId, WorkspaceId = 1, Title = "A52 hijack" });
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
        }
        // 3. Xóa bản ghi dựng tay: cả hai hình dạng đều không xóa được dòng của workspace khác.
        await using (var db = new SmartCvDbContext(options))
        {
            db.Jobs.Remove(new Job { JobId = foreignId, WorkspaceId = 2, Title = "x" });
            await Assert.ThrowsAsync<WorkspaceViolationException>(() => db.SaveChangesAsync());
        }
        await using (var db = new SmartCvDbContext(options))
        {
            db.Jobs.Remove(new Job { JobId = foreignId, WorkspaceId = 1, Title = "x" });
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
        }
        using (WorkspaceScope.Begin(2)) { await using var db = new SmartCvDbContext(options); Assert.Equal("A52 job 2", (await db.Jobs.SingleAsync(j => j.JobId == foreignId)).Title); }
    }

    [Fact]
    public async Task Own_workspace_entities_update_and_delete_normally()
    {
        var options = await A52Data.SeedAsync();
        using (WorkspaceScope.Begin(1))
        {
            await using var db = new SmartCvDbContext(options);
            var job = await db.Jobs.SingleAsync(j => j.Title == "A52 job 1");
            job.Description = "đã sửa";
            await db.SaveChangesAsync();
            db.Jobs.Add(new Job { Title = "A52 guard delete me" });
            await db.SaveChangesAsync();
            db.Jobs.Remove(await db.Jobs.SingleAsync(j => j.Title == "A52 guard delete me"));
            await db.SaveChangesAsync();
            Assert.Equal("đã sửa", (await db.Jobs.SingleAsync(j => j.Title == "A52 job 1")).Description);
        }
    }

    [Fact]
    public async Task Existing_history_and_stage_guards_still_work_inside_a_workspace()
    {
        var options = await A52Data.SeedAsync();
        using var scope = WorkspaceScope.Begin(1);
        await using var db = new SmartCvDbContext(options);
        // lịch sử chỉ-thêm
        var history = await db.ApplicationEvents.SingleAsync();
        history.Note = "sửa";
        Assert.Contains("chỉ được thêm", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync())).Message);
        db.ChangeTracker.Clear();
        // StageVersion vẫn đổi khi giai đoạn đổi
        var app = await db.Applications.SingleAsync();
        var before = app.StageVersion;
        app.Stage = ApplicationStages.Shortlisted;
        await db.SaveChangesAsync();
        Assert.NotEqual(before, app.StageVersion);
        // nhật ký xuất chỉ-thêm: sửa ExportLog vẫn bị chặn trước guard workspace
        db.ExportLogs.Add(new ExportLog { AuditEvent = new AuditEvent { ActionCode = "a", Category = "c", ResourceType = "r", Outcome = "Succeeded", RequestId = "r", SafeSummary = "s" }, RunId = 1, JobId = 1, Stage = "all", Fit = "all", RowCount = 1 });
        await db.SaveChangesAsync();
        var log = await db.ExportLogs.SingleAsync();
        Assert.Equal(1, log.WorkspaceId);
        log.Anonymized = true;
        Assert.Contains("chỉ được thêm", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync())).Message);
    }

    [Fact]
    public async Task Concurrent_saves_in_two_workspaces_stamp_independently()
    {
        var options = await A52Data.SeedAsync();
        async Task Write(int workspace, int i)
        {
            using var scope = WorkspaceScope.Begin(workspace);
            await Task.Yield();
            await using var db = new SmartCvDbContext(options);
            db.Jobs.Add(new Job { Title = $"A52 guard parallel {workspace}-{i}" });
            await db.SaveChangesAsync();
        }
        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() => Write(i % 2 + 1, i))));
        using var system = WorkspaceScope.BeginSystem("test: kiểm tra kết quả ghi song song");
        await using var check = new SmartCvDbContext(options);
        var rows = await check.Jobs.Where(j => j.Title.StartsWith("A52 guard parallel")).Select(j => new { j.Title, j.WorkspaceId }).ToListAsync();
        Assert.Equal(30, rows.Count);
        Assert.All(rows, r => Assert.Equal(r.Title["A52 guard parallel ".Length..].Split('-')[0], r.WorkspaceId.ToString()));
    }
}
