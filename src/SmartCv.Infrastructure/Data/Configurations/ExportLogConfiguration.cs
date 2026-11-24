using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

public sealed class ExportLogConfiguration : IEntityTypeConfiguration<ExportLog>
{
    public void Configure(EntityTypeBuilder<ExportLog> b)
    {
        b.ToTable("ExportLogs", t =>
        {
            t.HasTrigger("TR_ExportLogs_NoChange"); t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_ExportLogs_Rows", "[RowCount] > 0 AND [RowCount] <= 5000");
            t.HasCheckConstraint("CK_ExportLogs_Format", "[Format] COLLATE Latin1_General_100_BIN2 IN ('xlsx','pdf')");
            t.HasCheckConstraint("CK_ExportLogs_Stage", "[Stage] COLLATE Latin1_General_100_BIN2 IN ('all','New','Reviewing','Shortlisted','Interview','Rejected','OnHold')");
            t.HasCheckConstraint("CK_ExportLogs_Fit", "[Fit] COLLATE Latin1_General_100_BIN2 IN ('all','pass','notpass','unscored','excellent','good','potential','poor','issue')");
        });
        b.HasKey(e => e.ExportId);
        b.Property(e => e.WorkspaceId).IsRequired();
        b.HasOne<Workspace>().WithMany().HasForeignKey(e => e.WorkspaceId).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_ExportLogs_Workspaces");
        b.HasIndex(e => new { e.WorkspaceId, e.ExportId }).IsDescending(false, true).HasDatabaseName("IX_ExportLogs_Workspace_Export");
        b.Property(e => e.ExportId).UseIdentityColumn();
        b.Property(e => e.Format).HasMaxLength(8).IsUnicode(false).IsRequired();
        b.Property(e => e.Stage).HasMaxLength(20).IsUnicode(false).IsRequired();
        b.Property(e => e.Fit).HasMaxLength(20).IsUnicode(false).IsRequired();
        b.HasOne(e => e.AuditEvent).WithOne().HasForeignKey<ExportLog>(e => e.AuditEventId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<EvaluationRun>().WithMany().HasForeignKey(e => e.RunId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Job>().WithMany().HasForeignKey(e => e.JobId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(e => new { e.RunId, e.ExportId }).IsDescending(false, true);
        b.HasIndex(e => new { e.JobId, e.ExportId }).IsDescending(false, true);
    }
}
