using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data.Seed;

namespace SmartCv.Infrastructure.Data.Configurations;

public class JobSkillConfiguration : IEntityTypeConfiguration<JobSkill>
{
    public void Configure(EntityTypeBuilder<JobSkill> b)
    {
        b.ToTable("JobSkills", t =>
        {
            t.HasCheckConstraint("CK_JobSkills_SkillType", "SkillType IN (N'MustHave', N'NiceToHave')");
        });

        // Khóa chính ghép (thực thể yếu)
        b.HasKey(x => new { x.JobId, x.SkillCode }).HasName("PK_JobSkills");

        b.Property(x => x.SkillCode).HasMaxLength(50);
        b.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
        b.Property(x => x.SkillType).HasMaxLength(20).IsRequired();

        b.HasOne(x => x.Job)
            .WithMany(j => j.Skills)
            .HasForeignKey(x => x.JobId)
            .HasConstraintName("FK_JobSkills_Jobs")
            .OnDelete(DeleteBehavior.Restrict);

        // A3: dữ liệu nền, nạp qua migration SeedJobs
        b.HasData(JobSeedData.JobSkills);
    }
}
