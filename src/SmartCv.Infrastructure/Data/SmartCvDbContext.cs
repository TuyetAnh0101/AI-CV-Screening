using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data;

public class SmartCvDbContext : DbContext
{
    public SmartCvDbContext(DbContextOptions<SmartCvDbContext> options) : base(options)
    {
    }

    // 12 DbSet tương ứng 12 bảng trong SmartCvDb.sql
    public DbSet<Candidate> Candidates => Set<Candidate>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobSkill> JobSkills => Set<JobSkill>();
    public DbSet<JobSkillAlias> JobSkillAliases => Set<JobSkillAlias>();
    public DbSet<Cv> Cvs => Set<Cv>();
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<PromptVersion> PromptVersions => Set<PromptVersion>();
    public DbSet<EvaluationSet> EvaluationSets => Set<EvaluationSet>();
    public DbSet<HrLabel> HrLabels => Set<HrLabel>();
    public DbSet<EvaluationRun> EvaluationRuns => Set<EvaluationRun>();
    public DbSet<Evaluation> Evaluations => Set<Evaluation>();
    public DbSet<EvaluationSkill> EvaluationSkills => Set<EvaluationSkill>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Tự động nạp tất cả các class cấu hình trong thư mục Configurations
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SmartCvDbContext).Assembly);
    }
}
