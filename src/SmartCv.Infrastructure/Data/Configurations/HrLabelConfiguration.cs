using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

public class HrLabelConfiguration : IEntityTypeConfiguration<HrLabel>
{
    public void Configure(EntityTypeBuilder<HrLabel> b)
    {
        b.ToTable("HrLabels");

        b.HasKey(x => new { x.EvaluationSetId, x.ApplicationId }).HasName("PK_HrLabels");

        b.Property(x => x.Note).HasMaxLength(500);
        b.Property(x => x.LabeledBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.LabeledAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_HrLabels_LabeledAt");

        b.HasOne(x => x.EvaluationSet)
            .WithMany(s => s.HrLabels)
            .HasForeignKey(x => x.EvaluationSetId)
            .HasConstraintName("FK_HrLabels_EvaluationSets")
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.Application)
            .WithMany(a => a.HrLabels)
            .HasForeignKey(x => x.ApplicationId)
            .HasConstraintName("FK_HrLabels_Applications")
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.ApplicationId).HasDatabaseName("IX_HrLabels_ApplicationId");
    }
}
