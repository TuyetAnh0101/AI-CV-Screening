using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

public class EvaluationSetConfiguration : IEntityTypeConfiguration<EvaluationSet>
{
    public void Configure(EntityTypeBuilder<EvaluationSet> b)
    {
        b.ToTable("EvaluationSets");

        b.HasKey(x => x.EvaluationSetId).HasName("PK_EvaluationSets");
        b.Property(x => x.EvaluationSetId).UseIdentityColumn(1, 1);

        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.HasAlternateKey(x => x.Name).HasName("UQ_EvaluationSets_Name");

        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.CreatedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_EvaluationSets_CreatedAt");

        // SCApp v2: mục tiêu, Job chính và số phiên bản bộ (tăng khi thành viên/nhãn đổi).
        b.Property(x => x.Purpose).HasMaxLength(300);
        b.Property(x => x.Revision).HasDefaultValue(1, "DF_EvaluationSets_Revision").ValueGeneratedNever();
        b.Property(x => x.UpdatedAt).HasColumnType("datetime2");
        b.ToTable(t => t.HasCheckConstraint("CK_EvaluationSets_Revision", "[Revision] >= 1"));
        b.HasOne(x => x.Job).WithMany().HasForeignKey(x => x.JobId).IsRequired(false)
            .HasConstraintName("FK_EvaluationSets_Jobs").OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => x.JobId).HasDatabaseName("IX_EvaluationSets_JobId");
    }
}
