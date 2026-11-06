using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

public class EvaluationSkillConfiguration : IEntityTypeConfiguration<EvaluationSkill>
{
    public void Configure(EntityTypeBuilder<EvaluationSkill> b)
    {
        b.ToTable("EvaluationSkills");

        b.HasKey(x => new { x.EvaluationId, x.JobId, x.SkillCode }).HasName("PK_EvaluationSkills");

        b.Property(x => x.SkillCode).HasMaxLength(50);
        b.Property(x => x.Evidence).HasMaxLength(500);

        b.HasOne(x => x.Evaluation)
            .WithMany(e => e.Skills)
            .HasForeignKey(x => x.EvaluationId)
            .HasConstraintName("FK_EvaluationSkills_Evaluations")
            .OnDelete(DeleteBehavior.Restrict);

        // Khóa ngoại ghép (JobId, SkillCode) -> JobSkills
        b.HasOne(x => x.JobSkill)
            .WithMany(s => s.EvaluationSkills)
            .HasForeignKey(x => new { x.JobId, x.SkillCode })
            .HasConstraintName("FK_EvaluationSkills_JobSkills")
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.JobId, x.SkillCode }).HasDatabaseName("IX_EvaluationSkills_JobSkill");
    }
}
