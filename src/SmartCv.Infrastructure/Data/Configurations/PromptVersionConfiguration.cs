using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

public class PromptVersionConfiguration : IEntityTypeConfiguration<PromptVersion>
{
    public void Configure(EntityTypeBuilder<PromptVersion> b)
    {
        b.ToTable("PromptVersions");

        b.HasKey(x => x.PromptVersionId).HasName("PK_PromptVersions");
        b.Property(x => x.PromptVersionId).UseIdentityColumn(1, 1);

        b.Property(x => x.VersionName).HasMaxLength(100).IsRequired();
        // A51: tên phiên bản duy nhất trong workspace; đích khóa ngoại ghép từ EvaluationRuns.
        b.Property(x => x.WorkspaceId).IsRequired();
        b.HasAlternateKey(x => new { x.WorkspaceId, x.VersionName }).HasName("UQ_PromptVersions_Workspace_VersionName");
        b.HasAlternateKey(x => new { x.WorkspaceId, x.PromptVersionId }).HasName("AK_PromptVersions_Workspace_Prompt");
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId)
            .HasConstraintName("FK_PromptVersions_Workspaces").OnDelete(DeleteBehavior.NoAction);

        b.Property(x => x.SystemPrompt).IsRequired();      // NVARCHAR(MAX) NOT NULL
        b.Property(x => x.ChangeNote).HasMaxLength(500);
        b.Property(x => x.IsActive).HasDefaultValue(false, "DF_PromptVersions_IsActive").ValueGeneratedNever();
        b.Property(x => x.CreatedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_PromptVersions_CreatedAt");

        // Chỉ cho phép MỘT prompt đang được sử dụng MỖI workspace (chỉ mục UNIQUE có lọc)
        b.HasIndex(x => x.WorkspaceId)
            .IsUnique()
            .HasFilter("[IsActive] = 1")
            .HasDatabaseName("UX_PromptVersions_Workspace_OneActive");
    }
}
