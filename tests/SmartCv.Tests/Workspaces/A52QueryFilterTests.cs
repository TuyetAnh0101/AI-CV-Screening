using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using SmartCv.Core.Entities;
using SmartCv.Core.Workspaces;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Workspaces;

namespace SmartCv.Tests;

/// <summary>A52: query filter theo workspace (InMemory; bản dịch SQL được kiểm chứng riêng trong A52SqlTests).</summary>
public class A52QueryFilterTests
{
    private static readonly Type[] RootTypes =
        [typeof(Job), typeof(Cv), typeof(Candidate), typeof(Application), typeof(PromptVersion), typeof(EvaluationSet), typeof(EvaluationRun), typeof(ExportLog)];

    private static readonly Type[] ChildTypes =
        [typeof(JobSkill), typeof(JobSkillAlias), typeof(Evaluation), typeof(EvaluationSkill), typeof(ApplicationEvent), typeof(ApplicationFlag),
         typeof(ApplicationFlagEvent), typeof(HrLabel), typeof(EvaluationSetMember), typeof(CandidateDataRequest)];

    private static readonly Type[] GlobalTypes =
        [typeof(User), typeof(Workspace), typeof(WorkspaceMember), typeof(UserToken), typeof(UserExternalLogin), typeof(AuditEvent)];

    [Fact]
    public void Every_workspace_owned_entity_and_its_children_have_a_filter_and_global_data_has_none()
    {
        using var db = new SmartCvDbContext(A52Data.InMemory());
        var model = db.GetService<IDesignTimeModel>().Model;
        foreach (var type in RootTypes.Concat(ChildTypes))
            Assert.NotNull(model.FindEntityType(type)!.GetQueryFilter());
        foreach (var type in GlobalTypes)
            Assert.Null(model.FindEntityType(type)!.GetQueryFilter());
        // Mọi entity IWorkspaceOwned trong assembly Core đều nằm trong danh sách đã kiểm chứng (thêm entity mới mà quên filter => test đỏ).
        var owned = typeof(Job).Assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IWorkspaceOwned).IsAssignableFrom(t));
        Assert.Equal(RootTypes.Select(t => t.Name).Order(), owned.Select(t => t.Name).Order());
        Assert.Equal(RootTypes.Length + ChildTypes.Length, model.GetEntityTypes().Count(e => e.GetQueryFilter() is not null));
    }

    internal static readonly (string Name, Func<SmartCvDbContext, Task<int>> Count)[] Counters =
    [
        ("Jobs", db => db.Jobs.CountAsync()), ("Cvs", db => db.Cvs.CountAsync()), ("Candidates", db => db.Candidates.CountAsync()),
        ("Applications", db => db.Applications.CountAsync()), ("PromptVersions", db => db.PromptVersions.CountAsync()),
        ("EvaluationSets", db => db.EvaluationSets.CountAsync()), ("EvaluationRuns", db => db.EvaluationRuns.CountAsync()),
        ("ExportLogs", db => db.ExportLogs.CountAsync()), ("JobSkills", db => db.JobSkills.CountAsync()),
        ("JobSkillAliases", db => db.JobSkillAliases.CountAsync()), ("Evaluations", db => db.Evaluations.CountAsync()),
        ("EvaluationSkills", db => db.EvaluationSkills.CountAsync()), ("ApplicationEvents", db => db.ApplicationEvents.CountAsync()),
        ("ApplicationFlags", db => db.ApplicationFlags.CountAsync()), ("ApplicationFlagEvents", db => db.ApplicationFlagEvents.CountAsync()),
        ("HrLabels", db => db.HrLabels.CountAsync()), ("EvaluationSetMembers", db => db.EvaluationSetMembers.CountAsync()),
        ("CandidateDataRequests", db => db.CandidateDataRequests.CountAsync())
    ];

    [Fact]
    public async Task Without_a_workspace_every_filtered_set_returns_zero_rows_but_global_data_is_untouched()
    {
        var options = await A52Data.SeedAsync();
        await using var db = new SmartCvDbContext(options);
        Assert.False(WorkspaceScope.IsActive);
        foreach (var (name, count) in Counters) Assert.True(0 == await count(db), $"{name} phải trả 0 dòng khi không có workspace");
        Assert.Equal(2, await db.Workspaces.CountAsync());   // dữ liệu toàn cục không bị lọc
        // Các hình dạng truy vấn khác cũng đóng (Include, join, projection, Any, Find theo khóa không dùng ở đây)
        Assert.Empty(await db.Applications.Include(a => a.Job).Include(a => a.Cv).ThenInclude(c => c.Candidate).ToListAsync());
        Assert.False(await db.Jobs.AnyAsync());
        Assert.Empty(await (from a in db.Applications join j in db.Jobs on a.JobId equals j.JobId select j.Title).ToListAsync());
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public async Task A_workspace_sees_only_its_own_rows_in_every_set_including_children(int mine, int other)
    {
        var options = await A52Data.SeedAsync();
        using var scope = WorkspaceScope.Begin(mine);
        await using var db = new SmartCvDbContext(options);

        Assert.Equal([$"A52 job {mine}"], await db.Jobs.Where(j => j.Title.StartsWith("A52")).Select(j => j.Title).ToListAsync());
        Assert.Equal(1, await db.Cvs.CountAsync());
        Assert.Equal(1, await db.Candidates.CountAsync());
        Assert.Equal(1, await db.Applications.CountAsync());
        Assert.Equal(1, await db.PromptVersions.CountAsync());
        Assert.Equal(1, await db.EvaluationSets.CountAsync());
        Assert.Equal(1, await db.EvaluationRuns.CountAsync());
        Assert.Equal(1, await db.JobSkills.CountAsync(s => s.SkillCode.StartsWith("a52")));
        Assert.Equal(1, await db.JobSkillAliases.CountAsync());
        Assert.Equal(1, await db.Evaluations.CountAsync());
        Assert.Equal(1, await db.EvaluationSkills.CountAsync());
        Assert.Equal(1, await db.ApplicationEvents.CountAsync());
        Assert.Equal(1, await db.ApplicationFlags.CountAsync());
        Assert.Equal(1, await db.ApplicationFlagEvents.CountAsync());
        Assert.Equal(1, await db.HrLabels.CountAsync());
        Assert.Equal(1, await db.EvaluationSetMembers.CountAsync());
        Assert.Equal(1, await db.CandidateDataRequests.CountAsync());

        // Navigation / Include / Join / projection không làm lộ dữ liệu workspace khác.
        var apps = await db.Applications.Include(a => a.Job).Include(a => a.Cv).ThenInclude(c => c.Candidate)
            .Include(a => a.Evaluations).Include(a => a.Events).Include(a => a.HrLabels).ToListAsync();
        var app = Assert.Single(apps);
        Assert.Equal($"A52 job {mine}", app.Job.Title);
        Assert.Equal($"A52 candidate {mine}", app.Cv.Candidate!.FullName);
        Assert.Single(app.Evaluations); Assert.Single(app.Events); Assert.Single(app.HrLabels);
        Assert.Equal([$"A52 job {mine}"], await (from a in db.Applications join j in db.Jobs on a.JobId equals j.JobId select j.Title).ToListAsync());
        Assert.Equal([$"A52 job {mine}"], await db.Evaluations.Select(e => e.Application.Job.Title).ToListAsync());
        Assert.Equal([1], await db.Jobs.Where(j => j.Title.StartsWith("A52")).Select(j => j.Applications.Count).ToListAsync());
        Assert.Equal([$"a52s{mine}"], await db.EvaluationSkills.Select(s => s.SkillCode).ToListAsync());
        Assert.Empty(await db.Jobs.Where(j => j.Title == $"A52 job {other}").ToListAsync());

        // Include từ phía cha không kéo theo con của workspace khác.
        var job = await db.Jobs.Include(j => j.Skills).Include(j => j.Applications).SingleAsync(j => j.Title == $"A52 job {mine}");
        Assert.Equal(1, job.Skills.Count(s => s.SkillCode.StartsWith("a52")));
        Assert.Single(job.Applications);
    }

    [Fact]
    public async Task System_scope_sees_every_workspace_and_ends_with_the_scope()
    {
        var options = await A52Data.SeedAsync();
        await using var db = new SmartCvDbContext(options);
        using (WorkspaceScope.BeginSystem("test: kiểm tra system scope"))
        {
            Assert.Equal(2, await db.Applications.CountAsync());
            Assert.Equal(2, await db.Jobs.CountAsync(j => j.Title.StartsWith("A52")));
        }
        Assert.Equal(0, await db.Applications.CountAsync());
    }

    [Fact]
    public async Task One_context_instance_follows_the_scope_of_each_query_instead_of_freezing_the_first_workspace()
    {
        var options = await A52Data.SeedAsync();
        await using var db = new SmartCvDbContext(options);   // cùng một instance, cùng một model cache
        using (WorkspaceScope.Begin(1)) Assert.Equal(["A52 job 1"], await db.Jobs.Where(j => j.Title.StartsWith("A52")).Select(j => j.Title).ToListAsync());
        using (WorkspaceScope.Begin(2)) Assert.Equal(["A52 job 2"], await db.Jobs.Where(j => j.Title.StartsWith("A52")).Select(j => j.Title).ToListAsync());
        using (WorkspaceScope.Begin(1)) Assert.Equal(["A52 job 1"], await db.Jobs.Where(j => j.Title.StartsWith("A52")).Select(j => j.Title).ToListAsync());
        Assert.Empty(await db.Jobs.ToListAsync());
    }

    [Fact]
    public async Task Parallel_requests_for_two_workspaces_never_see_each_others_rows()
    {
        var options = await A52Data.SeedAsync();
        async Task<bool> Probe(int workspace, int round)
        {
            using var scope = WorkspaceScope.Begin(workspace);
            await Task.Yield();
            await using var db = new SmartCvDbContext(options);
            for (var i = 0; i < 5; i++)
            {
                await Task.Delay((workspace * 3 + round + i) % 7);
                var titles = await db.Jobs.Where(j => j.Title.StartsWith("A52")).Select(j => j.Title).ToListAsync();
                if (!titles.SequenceEqual([$"A52 job {workspace}"])) return false;
                if (await db.Applications.CountAsync() != 1) return false;
            }
            return true;
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => Probe(i % 2 + 1, i))));
        Assert.All(results, Assert.True);
        Assert.False(WorkspaceScope.IsActive);   // không scope nào rò sang luồng gọi
    }

    [Fact]
    public async Task Find_by_key_characterization_for_A53()
    {
        // Ghi nhận hành vi của EF để A53 không dựa vào Find/FindAsync: kết quả phản ánh việc query filter có (hay không) áp dụng cho Find.
        var options = await A52Data.SeedAsync();
        int otherJobId;
        using (WorkspaceScope.Begin(2)) { await using var db = new SmartCvDbContext(options); otherJobId = (await db.Jobs.SingleAsync(j => j.Title == "A52 job 2")).JobId; }
        using var scope = WorkspaceScope.Begin(1);
        await using var mine = new SmartCvDbContext(options);
        // Truy vấn có điều kiện khóa LUÔN bị lọc (đây là cách A53 phải dùng):
        Assert.Null(await mine.Jobs.FirstOrDefaultAsync(j => j.JobId == otherJobId));
        Assert.False(await mine.Jobs.AnyAsync(j => j.JobId == otherJobId));
        Assert.Equal(0, await mine.Jobs.CountAsync(j => j.JobId == otherJobId));
    }
}
