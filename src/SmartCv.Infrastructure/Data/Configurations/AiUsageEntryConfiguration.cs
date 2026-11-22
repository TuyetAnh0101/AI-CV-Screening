using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

public sealed class AiUsageEntryConfiguration : IEntityTypeConfiguration<AiUsageEntry>
{
    public void Configure(EntityTypeBuilder<AiUsageEntry> b)
    {
        b.ToTable("AiUsageEntries", t =>
        {
            t.HasCheckConstraint("CK_AiUsageEntries_Units", "[Units] > 0 AND [UnitsReleased] BETWEEN 0 AND [Units]");
            t.HasCheckConstraint("CK_AiUsageEntries_Source", "[Source] IN (N'Screening', N'PromptTest', N'Preview')");
            t.HasCheckConstraint("CK_AiUsageEntries_Run", "([Source] = N'Preview' AND [RunId] IS NULL) OR ([Source] <> N'Preview' AND [RunId] IS NOT NULL)");
        });
        b.HasKey(x => x.AiUsageEntryId);
        b.Property(x => x.AiUsageEntryId).UseIdentityColumn();
        b.Property(x => x.WorkspaceId).IsRequired();
        b.Property(x => x.Source).HasMaxLength(20).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2").IsRequired();
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId)
            .OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => new { x.WorkspaceId, x.CreatedAtUtc });
        b.HasIndex(x => new { x.WorkspaceId, x.RunId }).IsUnique()
            .HasFilter("[RunId] IS NOT NULL");
    }
}
