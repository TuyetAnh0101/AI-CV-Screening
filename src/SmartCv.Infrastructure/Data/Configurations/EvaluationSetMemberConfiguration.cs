using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

/// <summary>SCApp v2: thành viên bộ CV đánh giá, tách khỏi nhãn HR.</summary>
public class EvaluationSetMemberConfiguration : IEntityTypeConfiguration<EvaluationSetMember>
{
    public void Configure(EntityTypeBuilder<EvaluationSetMember> b)
    {
        b.ToTable("EvaluationSetMembers");
        b.HasKey(x => new { x.EvaluationSetId, x.ApplicationId }).HasName("PK_EvaluationSetMembers");
        b.Property(x => x.AddedBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.AddedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_EvaluationSetMembers_AddedAt");

        b.HasOne(x => x.EvaluationSet).WithMany(s => s.Members).HasForeignKey(x => x.EvaluationSetId)
            .HasConstraintName("FK_EvaluationSetMembers_EvaluationSets").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Application).WithMany(a => a.EvaluationSetMemberships).HasForeignKey(x => x.ApplicationId)
            .HasConstraintName("FK_EvaluationSetMembers_Applications").OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ApplicationId).HasDatabaseName("IX_EvaluationSetMembers_ApplicationId");
    }
}
