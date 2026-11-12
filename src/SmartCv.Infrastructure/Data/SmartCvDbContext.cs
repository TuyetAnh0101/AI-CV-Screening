using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data;

public class SmartCvDbContext : DbContext
{
    public SmartCvDbContext(DbContextOptions<SmartCvDbContext> options) : base(options)
    {
    }

    // Các bảng nghiệp vụ, bao gồm lịch sử quyết định chỉ thêm của A35.
    public DbSet<Candidate> Candidates => Set<Candidate>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobSkill> JobSkills => Set<JobSkill>();
    public DbSet<JobSkillAlias> JobSkillAliases => Set<JobSkillAlias>();
    public DbSet<Cv> Cvs => Set<Cv>();
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<ApplicationEvent> ApplicationEvents => Set<ApplicationEvent>();
    public DbSet<PromptVersion> PromptVersions => Set<PromptVersion>();
    public DbSet<EvaluationSet> EvaluationSets => Set<EvaluationSet>();
    public DbSet<HrLabel> HrLabels => Set<HrLabel>();
    public DbSet<EvaluationRun> EvaluationRuns => Set<EvaluationRun>();
    public DbSet<Evaluation> Evaluations => Set<Evaluation>();
    public DbSet<EvaluationSkill> EvaluationSkills => Set<EvaluationSkill>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareStageChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareStageChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void PrepareStageChanges()
    {
        ChangeTracker.DetectChanges();
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
    }
}
