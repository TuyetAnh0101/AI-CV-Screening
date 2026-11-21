using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;
using SmartCv.Core.Workspaces;

namespace SmartCv.Infrastructure.Data.Configurations;

/// <summary>A51: workspace. WorkspaceId = 1 (Internal) được nạp qua HasData; OwnerUserId do migration cập nhật bằng SQL.</summary>
public class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public static readonly DateTime InternalCreatedAt = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    public void Configure(EntityTypeBuilder<Workspace> b)
    {
        b.ToTable("Workspaces", t =>
        {
            t.HasCheckConstraint("CK_Workspaces_Kind", $"[Kind] COLLATE Latin1_General_100_BIN2 IN ({string.Join(", ", WorkspaceKinds.All.Select(k => $"'{k}'"))})");
            t.HasCheckConstraint("CK_Workspaces_DailyAiEvaluationLimit", "[DailyAiEvaluationLimit] IS NULL OR [DailyAiEvaluationLimit] >= 0");
            t.HasCheckConstraint("CK_Workspaces_MaxCvCount", "[MaxCvCount] IS NULL OR [MaxCvCount] >= 0");
        });

        b.HasKey(x => x.WorkspaceId).HasName("PK_Workspaces");
        b.Property(x => x.WorkspaceId).UseIdentityColumn(1, 1);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(20).IsUnicode(false).IsRequired();
        b.Property(x => x.IsActive).HasDefaultValue(true, "DF_Workspaces_IsActive").ValueGeneratedNever();
        b.Property(x => x.CreatedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_Workspaces_CreatedAt");

        // Mỗi người dùng chỉ có tối đa một workspace cá nhân (MVP: một tài khoản - một workspace).
        b.HasIndex(x => x.OwnerUserId).IsUnique().HasFilter("[Kind] = 'Personal'").HasDatabaseName("UX_Workspaces_PersonalOwner");

        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerUserId).IsRequired(false)
            .HasConstraintName("FK_Workspaces_Users_Owner").OnDelete(DeleteBehavior.NoAction);

        b.HasData(new
        {
            WorkspaceId = WorkspaceKinds.InternalWorkspaceId,
            Name = "SCApp nội bộ",
            Kind = WorkspaceKinds.Internal,
            IsActive = true,
            CreatedAt = InternalCreatedAt,
        });
    }
}
