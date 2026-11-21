using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;
using SmartCv.Core.Workspaces;

namespace SmartCv.Infrastructure.Data.Configurations;

/// <summary>A51: thành viên workspace. Role (Owner/Member) độc lập với Users.Role (Admin/HR).</summary>
public class WorkspaceMemberConfiguration : IEntityTypeConfiguration<WorkspaceMember>
{
    public void Configure(EntityTypeBuilder<WorkspaceMember> b)
    {
        b.ToTable("WorkspaceMembers", t => t.HasCheckConstraint("CK_WorkspaceMembers_Role",
            $"[Role] COLLATE Latin1_General_100_BIN2 IN ({string.Join(", ", WorkspaceRoles.All.Select(r => $"'{r}'"))})"));

        b.HasKey(x => new { x.WorkspaceId, x.UserId }).HasName("PK_WorkspaceMembers");
        b.Property(x => x.Role).HasMaxLength(10).IsUnicode(false).IsRequired();
        b.Property(x => x.JoinedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_WorkspaceMembers_JoinedAt");

        b.HasOne(x => x.Workspace).WithMany(w => w.Members).HasForeignKey(x => x.WorkspaceId)
            .HasConstraintName("FK_WorkspaceMembers_Workspaces").OnDelete(DeleteBehavior.NoAction);
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
            .HasConstraintName("FK_WorkspaceMembers_Users").OnDelete(DeleteBehavior.NoAction);

        b.HasIndex(x => x.UserId).HasDatabaseName("IX_WorkspaceMembers_UserId");
        // Mỗi workspace có tối đa một Owner.
        b.HasIndex(x => x.WorkspaceId).IsUnique().HasFilter("[Role] = 'Owner'").HasDatabaseName("UX_WorkspaceMembers_OneOwner");
    }
}
