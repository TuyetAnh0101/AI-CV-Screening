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
    }
}
