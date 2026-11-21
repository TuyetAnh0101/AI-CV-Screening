using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public const string NoChangeTrigger = "TR_AuditEvents_NoChange";
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.ToTable("AuditEvents", t =>
        {
            t.HasTrigger(NoChangeTrigger); t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_AuditEvents_Outcome", "[Outcome] COLLATE Latin1_General_100_BIN2 IN ('Succeeded','Denied','Failed','Partial','NoChange','AccessGranted')");
            t.HasCheckConstraint("CK_AuditEvents_Actor", "([ActorUserId] IS NULL AND [ActorNameSnapshot] IS NULL AND [ActorRoleSnapshot] IS NULL) OR ([ActorUserId] IS NOT NULL AND [ActorUserId] > 0 AND [ActorNameSnapshot] IS NOT NULL AND LEN([ActorNameSnapshot]) > 0 AND [ActorRoleSnapshot] IS NOT NULL AND [ActorRoleSnapshot] COLLATE Latin1_General_100_BIN2 IN (N'Admin',N'HR'))");
        });
        b.HasKey(e => e.AuditEventId).HasName("PK_AuditEvents");
        b.Property(e => e.AuditEventId).UseIdentityColumn();
        b.Property(e => e.OccurredAtUtc).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()");
        b.Property(e => e.ActorNameSnapshot).HasMaxLength(150);
        b.Property(e => e.ActorRoleSnapshot).HasMaxLength(20);
        b.Property(e => e.ActionCode).HasMaxLength(80).IsUnicode(false).IsRequired();
        b.Property(e => e.Category).HasMaxLength(32).IsUnicode(false).IsRequired();
        b.Property(e => e.ResourceType).HasMaxLength(40).IsUnicode(false).IsRequired();
        b.Property(e => e.ResourceId).HasMaxLength(80).IsUnicode(false);
        b.Property(e => e.Outcome).HasMaxLength(20).IsUnicode(false).IsRequired();
        b.Property(e => e.RequestId).HasMaxLength(64).IsUnicode(false).IsRequired();
        b.Property(e => e.SafeSummary).HasMaxLength(300).IsRequired();
        b.Property(e => e.FailureCode).HasMaxLength(64).IsUnicode(false);
        // A51: nullable; sự kiện hệ thống và lịch sử cũ giữ NULL (bảng append-only, trigger chặn UPDATE).
        b.HasOne<Workspace>().WithMany().HasForeignKey(e => e.WorkspaceId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_AuditEvents_Workspaces");
        b.HasIndex(e => new { e.WorkspaceId, e.OccurredAtUtc }).IsDescending(false, true).HasDatabaseName("IX_AuditEvents_Workspace_Time");
        b.HasOne<User>().WithMany().HasForeignKey(e => e.ActorUserId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_AuditEvents_Users");
        b.HasIndex(e => new { e.OccurredAtUtc, e.AuditEventId }).IsDescending(true, true).HasDatabaseName("IX_AuditEvents_Time");
        b.HasIndex(e => new { e.ActorUserId, e.OccurredAtUtc }).IsDescending(false, true).HasDatabaseName("IX_AuditEvents_Actor_Time");
        b.HasIndex(e => new { e.ActionCode, e.OccurredAtUtc }).IsDescending(false, true).HasDatabaseName("IX_AuditEvents_Action_Time");
        b.HasIndex(e => new { e.Category, e.Outcome, e.OccurredAtUtc }).IsDescending(false, false, true).HasDatabaseName("IX_AuditEvents_Category_Outcome_Time");
        b.HasIndex(e => new { e.ResourceType, e.ResourceId, e.OccurredAtUtc }).IsDescending(false, false, true).HasDatabaseName("IX_AuditEvents_Resource_Time");
        b.HasIndex(e => e.RequestId).HasDatabaseName("IX_AuditEvents_Request");
    }
}
