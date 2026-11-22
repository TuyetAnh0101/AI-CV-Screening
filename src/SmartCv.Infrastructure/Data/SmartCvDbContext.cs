using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Entities;
using SmartCv.Core.Workspaces;
using SmartCv.Infrastructure.Workspaces;

namespace SmartCv.Infrastructure.Data;

public class SmartCvDbContext : DbContext
{
    private readonly IWorkspaceContext workspace;

    /// <param name="workspace">
    /// Ngữ cảnh workspace. Request: HttpWorkspaceContext (đã xác nhận với database). Không truyền (worker, test, design-time):
    /// dùng <see cref="WorkspaceScope.Ambient"/>, tức là chỉ có workspace khi code mở <see cref="WorkspaceScope.Begin"/>; không có thì fail-closed.
    /// </param>
    public SmartCvDbContext(DbContextOptions<SmartCvDbContext> options, IWorkspaceContext? workspace = null) : base(options)
    {
        this.workspace = workspace ?? WorkspaceScope.Ambient;
    }

    // EF đọc các thuộc tính này cho MỖI lần thực thi truy vấn (tham số hóa theo instance), không đóng băng theo model cache.
    // 0 không phải mã workspace hợp lệ (identity bắt đầu từ 1) nên "không có workspace" không khớp dòng nào.
    private int CurrentWorkspaceId => workspace.WorkspaceId ?? 0;
    private bool InSystemScope => workspace.IsSystemScope;

    // Các bảng nghiệp vụ, bao gồm lịch sử quyết định chỉ thêm của A35.
    public DbSet<Candidate> Candidates => Set<Candidate>();
    public DbSet<CandidateDataRequest> CandidateDataRequests => Set<CandidateDataRequest>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobSkill> JobSkills => Set<JobSkill>();
    public DbSet<JobSkillAlias> JobSkillAliases => Set<JobSkillAlias>();
    public DbSet<Cv> Cvs => Set<Cv>();
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<ApplicationEvent> ApplicationEvents => Set<ApplicationEvent>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<UserToken> UserTokens => Set<UserToken>();
    public DbSet<UserExternalLogin> UserExternalLogins => Set<UserExternalLogin>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<ExportLog> ExportLogs => Set<ExportLog>();
    public DbSet<PromptVersion> PromptVersions => Set<PromptVersion>();
    public DbSet<EvaluationSet> EvaluationSets => Set<EvaluationSet>();
    public DbSet<HrLabel> HrLabels => Set<HrLabel>();
    public DbSet<EvaluationSetMember> EvaluationSetMembers => Set<EvaluationSetMember>();
    public DbSet<ApplicationFlag> ApplicationFlags => Set<ApplicationFlag>();
    public DbSet<ApplicationFlagEvent> ApplicationFlagEvents => Set<ApplicationFlagEvent>();
    public DbSet<EvaluationRun> EvaluationRuns => Set<EvaluationRun>();
    public DbSet<Evaluation> Evaluations => Set<Evaluation>();
    public DbSet<EvaluationSkill> EvaluationSkills => Set<EvaluationSkill>();

    public DbSet<AiUsageEntry> AiUsageEntries => Set<AiUsageEntry>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareStageChanges();
        EnforceWorkspaceBoundary();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareStageChanges();
        EnforceWorkspaceBoundary();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void PrepareStageChanges()
    {
        ChangeTracker.DetectChanges();
        if (ChangeTracker.Entries<ExportLog>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Nhật ký xuất chỉ được thêm, không được sửa hoặc xóa.");
        if (ChangeTracker.Entries<AuditEvent>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Nhật ký hoạt động chỉ được thêm, không được sửa hoặc xóa.");
        if (ChangeTracker.Entries<ApplicationEvent>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Lịch sử quyết định chỉ được thêm, không được sửa hoặc xóa.");

        foreach (var entry in ChangeTracker.Entries<Application>().Where(e => e.State == EntityState.Modified))
        {
            if (entry.Property(a => a.Stage).IsModified || entry.Property(a => a.RejectReason).IsModified)
                entry.Entity.StageVersion = Guid.NewGuid();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Tự động nạp tất cả các class cấu hình trong thư mục Configurations
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SmartCvDbContext).Assembly);

        // A52: WorkspaceId là concurrency token của mọi entity gốc. UPDATE/DELETE trở thành "... WHERE Id = @id AND WorkspaceId = @gốc",
        // nên một entity dựng tay (Attach/Update) mang WorkspaceId của mình nhưng khóa của bản ghi workspace khác sẽ ghi 0 dòng
        // (DbUpdateConcurrencyException) thay vì sửa/xóa nhầm dữ liệu workspace khác. Không đổi schema.
        foreach (var owned in modelBuilder.Model.GetEntityTypes().Where(t => typeof(IWorkspaceOwned).IsAssignableFrom(t.ClrType)).ToList())
            modelBuilder.Entity(owned.ClrType).Property(nameof(IWorkspaceOwned.WorkspaceId)).IsConcurrencyToken().ValueGeneratedNever();

        // A52 - lớp truy vấn (fail-closed): mọi entity gốc lọc theo workspace hiện hành; entity con lọc qua cha.
        // Users, Workspaces, WorkspaceMembers, UserTokens, UserExternalLogins, AuditEvents là dữ liệu toàn cục/hệ thống: không lọc.
        modelBuilder.Entity<Job>().HasQueryFilter(e => InSystemScope || e.WorkspaceId == CurrentWorkspaceId);
        modelBuilder.Entity<Cv>().HasQueryFilter(e => InSystemScope || e.WorkspaceId == CurrentWorkspaceId);
        modelBuilder.Entity<Candidate>().HasQueryFilter(e => InSystemScope || e.WorkspaceId == CurrentWorkspaceId);
        modelBuilder.Entity<Application>().HasQueryFilter(e => InSystemScope || e.WorkspaceId == CurrentWorkspaceId);
        modelBuilder.Entity<PromptVersion>().HasQueryFilter(e => InSystemScope || e.WorkspaceId == CurrentWorkspaceId);
        modelBuilder.Entity<EvaluationSet>().HasQueryFilter(e => InSystemScope || e.WorkspaceId == CurrentWorkspaceId);
        modelBuilder.Entity<EvaluationRun>().HasQueryFilter(e => InSystemScope || e.WorkspaceId == CurrentWorkspaceId);
        modelBuilder.Entity<AiUsageEntry>()
            .HasQueryFilter(e => InSystemScope || e.WorkspaceId == CurrentWorkspaceId);
        modelBuilder.Entity<ExportLog>().HasQueryFilter(e => InSystemScope || e.WorkspaceId == CurrentWorkspaceId);

        modelBuilder.Entity<JobSkill>().HasQueryFilter(e => InSystemScope || e.Job.WorkspaceId == CurrentWorkspaceId);
        modelBuilder.Entity<JobSkillAlias>().HasQueryFilter(e => InSystemScope || e.JobSkill.Job.WorkspaceId == CurrentWorkspaceId);
        modelBuilder.Entity<Evaluation>().HasQueryFilter(e => InSystemScope || e.Run.WorkspaceId == CurrentWorkspaceId);
        modelBuilder.Entity<EvaluationSkill>().HasQueryFilter(e => InSystemScope || e.Evaluation.Run.WorkspaceId == CurrentWorkspaceId);
        modelBuilder.Entity<ApplicationEvent>().HasQueryFilter(e => InSystemScope || e.Application.WorkspaceId == CurrentWorkspaceId);
        modelBuilder.Entity<ApplicationFlag>().HasQueryFilter(e => InSystemScope || e.Application.WorkspaceId == CurrentWorkspaceId);
        modelBuilder.Entity<ApplicationFlagEvent>().HasQueryFilter(e => InSystemScope || e.Flag.Application.WorkspaceId == CurrentWorkspaceId);
        modelBuilder.Entity<HrLabel>().HasQueryFilter(e => InSystemScope || e.EvaluationSet.WorkspaceId == CurrentWorkspaceId);
        modelBuilder.Entity<EvaluationSetMember>().HasQueryFilter(e => InSystemScope || e.EvaluationSet.WorkspaceId == CurrentWorkspaceId);
        // Journal ownership survives candidate deletion; it must never be inferred from a deleted principal.
        modelBuilder.Entity<CandidateDataRequest>().HasQueryFilter(e => InSystemScope || e.WorkspaceId == CurrentWorkspaceId);
    }

    /// <summary>
    /// A52 - lớp ghi: entity thuộc workspace tự nhận WorkspaceId hiện hành (khi còn 0), không được ghi/sửa/xóa vào workspace khác
    /// và không được đổi WorkspaceId. Không có workspace (và không phải system scope) thì không ghi được entity nghiệp vụ.
    /// Chạy sau PrepareStageChanges (đã DetectChanges) nên thấy cả thay đổi của entity chưa tải trước (Attach/Update).
    /// </summary>
    private void EnforceWorkspaceBoundary()
    {
        var current = workspace.WorkspaceId;
        var system = workspace.IsSystemScope;
        foreach (var entry in ChangeTracker.Entries<IWorkspaceOwned>().ToList())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached) continue;
            var property = entry.Property(nameof(IWorkspaceOwned.WorkspaceId));
            var operation = entry.State.ToString();
            switch (entry.State)
            {
                case EntityState.Added:
                    // Entity mới chưa gán (WorkspaceId = Unassigned, hoặc 0) tự nhận workspace hiện hành. Dùng Unassigned (-1) thay vì 0 làm mặc định
                    // vì EF coi khóa ngoại thuộc khóa thay thế có giá trị CLR mặc định (0) là "chưa biết" và không cho sửa lại lúc lưu.
                    if ((int)property.CurrentValue! is WorkspaceKinds.UnassignedWorkspaceId or 0 && current is { } stamp)
                        property.CurrentValue = stamp;
                    var added = (int)property.CurrentValue!;
                    if (added <= 0 || (!system && (current is null || added != current))) throw Violation(entry, operation);
                    break;
                case EntityState.Modified:
                    // Giá trị gốc quyết định entity thuộc workspace nào trong database; giá trị hiện tại không được khác gốc.
                    var original = (int)property.OriginalValue!;
                    if ((int)property.CurrentValue! != original || (!system && (current is null || original != current))) throw Violation(entry, operation);
                    break;
                case EntityState.Deleted:
                    if (!system && (current is null || (int)property.OriginalValue! != current)) throw Violation(entry, operation);
                    break;
            }
        }
    }

    private WorkspaceViolationException Violation(EntityEntry entry, string operation)
    {
        // Chỉ ghi loại entity và thao tác: không ghi mã workspace, khóa bản ghi hay giá trị (có thể là dữ liệu CV).
        try
        {
            this.GetService<ILoggerFactory>()?.CreateLogger("SmartCv.Workspaces.Guard")
                .LogError("Từ chối ghi vi phạm workspace: {Entity} {Operation}; có workspace: {HasWorkspace}; system scope: {System}.",
                    entry.Metadata.ClrType.Name, operation, workspace.WorkspaceId is not null, workspace.IsSystemScope);
        }
        catch (Exception) { /* log là phụ; không được che lỗi chính */ }
        return new WorkspaceViolationException(entry.Metadata.ClrType.Name, operation);
    }
}
