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
        // A51: tên bộ duy nhất trong workspace; đích khóa ngoại ghép từ EvaluationRuns.
        b.Property(x => x.WorkspaceId).IsRequired();
        b.HasAlternateKey(x => new { x.WorkspaceId, x.Name }).HasName("UQ_EvaluationSets_Workspace_Name");
        b.HasAlternateKey(x => new { x.WorkspaceId, x.EvaluationSetId }).HasName("AK_EvaluationSets_Workspace_Set");
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId)
            .HasConstraintName("FK_EvaluationSets_Workspaces").OnDelete(DeleteBehavior.NoAction);

        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.CreatedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_EvaluationSets_CreatedAt");

        // SCApp v2: mục tiêu, Job chính và số phiên bản bộ (tăng khi thành viên/nhãn đổi).
        b.Property(x => x.Purpose).HasMaxLength(300);
        b.Property(x => x.Revision).HasDefaultValue(1, "DF_EvaluationSets_Revision").ValueGeneratedNever();
        b.Property(x => x.UpdatedAt).HasColumnType("datetime2");
        b.ToTable(t => t.HasCheckConstraint("CK_EvaluationSets_Revision", "[Revision] >= 1"));
        // A51: khóa ngoại ghép (WorkspaceId, JobId): bộ không thể trỏ tới Job của workspace khác. JobId null thì không kiểm tra.
        // SET NULL không dùng được vì sẽ xóa cả WorkspaceId (NOT NULL); hiện không có chức năng xóa Job nên dùng Restrict.
        b.HasOne(x => x.Job).WithMany().HasForeignKey(x => new { x.WorkspaceId, x.JobId })
            .HasPrincipalKey(j => new { j.WorkspaceId, j.JobId }).IsRequired(false)
            .HasConstraintName("FK_EvaluationSets_Jobs").OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.JobId).HasDatabaseName("IX_EvaluationSets_JobId");
    }
}
