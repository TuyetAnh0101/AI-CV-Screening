using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;
using SmartCv.Core.Workspaces;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Workspaces;

namespace SmartCv.Tests;

/// <summary>Dữ liệu tổng hợp cho test A52: hai workspace, mỗi bên một đồ thị dữ liệu đầy đủ (gốc + con).</summary>
internal static class A52Data
{
    internal static DbContextOptions<SmartCvDbContext> InMemory(string? name = null) =>
        new DbContextOptionsBuilder<SmartCvDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString("N")).Options;

    /// <summary>InMemory: workspace nội bộ (WorkspaceId = 1) như migration A51 tạo. Không làm gì với SQL Server (đã có từ migration).</summary>
    internal static async Task EnsureInternalWorkspaceAsync(this SmartCvDbContext db)
    {
        if (!db.Database.IsInMemory()) return;
        if (await db.Workspaces.AnyAsync(w => w.WorkspaceId == WorkspaceKinds.InternalWorkspaceId)) return;
        db.Workspaces.Add(new Workspace { WorkspaceId = 1, Name = "SCApp nội bộ", Kind = WorkspaceKinds.Internal, IsActive = true, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    /// <summary>Hai workspace (1 = Internal, 2 = Personal), mỗi bên một bộ dữ liệu đầy đủ. Workspace 1 không dùng HasData job mẫu.</summary>
    internal static async Task<DbContextOptions<SmartCvDbContext>> SeedAsync(DbContextOptions<SmartCvDbContext>? options = null)
    {
        options ??= InMemory();
        await using (var db = new SmartCvDbContext(options))
        {
            await db.EnsureInternalWorkspaceAsync();
            if (!await db.Workspaces.AnyAsync(w => w.WorkspaceId == 2))
            {
                // SQL Server: WorkspaceId là cột identity (workspace 1 do migration tạo) nên workspace mới tự nhận 2.
                var second = new Workspace { Name = "Không gian cá nhân", Kind = WorkspaceKinds.Personal, IsActive = true, CreatedAt = DateTime.UtcNow };
                if (db.Database.IsInMemory()) second.WorkspaceId = 2;
                db.Workspaces.Add(second);
                await db.SaveChangesAsync();
            }
        }
        foreach (var workspace in new[] { 1, 2 })
            using (WorkspaceScope.Begin(workspace))
            {
                await using var db = new SmartCvDbContext(options);
                AddGraph(db, workspace);
                await db.SaveChangesAsync();
                var candidateId = (await db.Candidates.SingleAsync(c => c.FullName == $"A52 candidate {workspace}")).CandidateId;
                db.CandidateDataRequests.Add(new CandidateDataRequest { CandidateId = candidateId, RequestType = "Export", HandledBy = "A52", CreatedAt = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
        return options;
    }

    internal static string Marker(int workspace) => $"A52 {workspace}";

    private static void AddGraph(SmartCvDbContext db, int w)
    {
        var job = new Job { Title = $"A52 job {w}", MinYearsExperience = 0, CreatedAt = DateTime.UtcNow };
        var skill = new JobSkill { Job = job, SkillCode = $"a52s{w}", DisplayName = "S", SkillType = SkillTypes.MustHave };
        var alias = new JobSkillAlias { JobSkill = skill, Alias = $"a52alias{w}" };
        var candidate = new Candidate { FullName = $"A52 candidate {w}", CreatedAt = DateTime.UtcNow };
        var cv = new Cv
        {
            CvId = Guid.NewGuid(), Candidate = candidate, OriginalFileName = $"a52-{w}.pdf", StoredPath = $"a52/{w}.pdf",
            ContentType = "application/pdf", FileSizeBytes = 1, FileHash = new string((char)('a' + w), 64), UploadedAt = DateTime.UtcNow
        };
        var app = new Application { Cv = cv, Job = job, CreatedAt = DateTime.UtcNow };
        var history = new ApplicationEvent { Application = app, FromStage = ApplicationStages.New, ToStage = ApplicationStages.Reviewing, Actor = "A52", CreatedAt = DateTime.UtcNow };
        var flag = new ApplicationFlag { Application = app, Reason = ManualFlagReasons.All[0], CreatedBy = "A52", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var flagEvent = new ApplicationFlagEvent { Flag = flag, Action = ManualFlagActions.All[0], Actor = "A52", CreatedAt = DateTime.UtcNow };
        var prompt = new PromptVersion { VersionName = $"A52 prompt {w}", SystemPrompt = "p", IsActive = true, CreatedAt = DateTime.UtcNow };
        var set = new EvaluationSet { Name = $"A52 set {w}", Job = job, CreatedAt = DateTime.UtcNow };
        var label = new HrLabel { EvaluationSet = set, Application = app, IsPass = true, LabeledBy = "A52", LabeledAt = DateTime.UtcNow };
        var member = new EvaluationSetMember { EvaluationSet = set, Application = app, AddedBy = "A52", AddedAt = DateTime.UtcNow };
        var run = new EvaluationRun
        {
            RunType = RunTypes.Screening, PromptVersion = prompt, ModelName = "fake", KeywordWeight = .3m, AiWeight = .7m,
            Status = RunStatuses.Completed, TotalItems = 1, ProcessedItems = 1, CreatedAt = DateTime.UtcNow
        };
        var evaluation = new Evaluation { Run = run, Application = app, Status = EvaluationStatuses.Completed, CreatedAt = DateTime.UtcNow };
        var evaluated = new EvaluationSkill { Evaluation = evaluation, JobSkill = skill, IsMatched = true };
        db.AddRange(job, skill, alias, candidate, cv, app, history, flag, flagEvent, prompt, set, label, member, run, evaluation, evaluated);
    }
}

/// <summary>
/// CHỈ cho test host: request thật dùng đúng HttpWorkspaceContext (đã xác nhận với database, fail-closed). Code test chạy NGOÀI request
/// (gieo dữ liệu / đọc kiểm tra qua factory.Services.CreateScope()) không có HttpContext nên mặc định làm việc trong workspace nội bộ (1),
/// trừ khi test mở WorkspaceScope riêng. Không áp dụng cho các test A52 tự dựng SmartCvDbContext (ở đó không có workspace là fail-closed).
/// </summary>
internal sealed class TestHostWorkspaceContext(Microsoft.AspNetCore.Http.IHttpContextAccessor accessor) : IWorkspaceContext
{
    private readonly SmartCv.Api.Auth.HttpWorkspaceContext request = new(accessor);
    private bool InRequest => accessor.HttpContext is not null;
    public int? WorkspaceId => WorkspaceScope.IsActive ? WorkspaceScope.Ambient.WorkspaceId : InRequest ? request.WorkspaceId : WorkspaceKinds.InternalWorkspaceId;
    public string? Role => WorkspaceScope.IsActive ? WorkspaceScope.Ambient.Role : InRequest ? request.Role : null;
    public string? Kind => WorkspaceScope.IsActive ? WorkspaceScope.Ambient.Kind : InRequest ? request.Kind : WorkspaceKinds.Internal;
    public bool IsSystemScope => WorkspaceScope.Ambient.IsSystemScope;
}
