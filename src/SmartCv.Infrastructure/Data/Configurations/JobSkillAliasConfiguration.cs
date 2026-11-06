using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

public class JobSkillAliasConfiguration : IEntityTypeConfiguration<JobSkillAlias>
{
    public void Configure(EntityTypeBuilder<JobSkillAlias> b)
    {
        b.ToTable("JobSkillAliases");

        b.HasKey(x => new { x.JobId, x.SkillCode, x.Alias }).HasName("PK_JobSkillAliases");

        b.Property(x => x.SkillCode).HasMaxLength(50);
        b.Property(x => x.Alias).HasMaxLength(100);

        // Khóa ngoại ghép (JobId, SkillCode) -> JobSkills
        b.HasOne(x => x.JobSkill)
            .WithMany(s => s.Aliases)
            .HasForeignKey(x => new { x.JobId, x.SkillCode })
            .HasConstraintName("FK_JobSkillAliases_JobSkills")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
