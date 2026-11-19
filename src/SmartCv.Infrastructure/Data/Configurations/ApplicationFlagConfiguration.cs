using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

/// <summary>SCApp v2: cờ thủ công của HR và lịch sử cờ.</summary>
public class ApplicationFlagConfiguration : IEntityTypeConfiguration<ApplicationFlag>
{
    private static readonly string Reasons = string.Join(", ", ManualFlagReasons.All.Select(r => $"N'{r}'"));
    private static readonly string Statuses = string.Join(", ", ManualFlagStatuses.All.Select(r => $"N'{r}'"));

    public void Configure(EntityTypeBuilder<ApplicationFlag> b)
    {
        b.ToTable("ApplicationFlags", t =>
        {
            t.HasCheckConstraint("CK_ApplicationFlags_Reason", $"[Reason] COLLATE Latin1_General_100_BIN2 IN ({Reasons})");
            t.HasCheckConstraint("CK_ApplicationFlags_Status", $"[Status] COLLATE Latin1_General_100_BIN2 IN ({Statuses})");
            t.HasCheckConstraint("CK_ApplicationFlags_CreatedBy", "LEN(LTRIM(RTRIM([CreatedBy]))) > 0");
            t.HasCheckConstraint("CK_ApplicationFlags_Resolved",
                "([Status] = N'Open' AND [ResolvedAt] IS NULL AND [ResolvedBy] IS NULL) OR ([Status] = N'Resolved' AND [ResolvedAt] IS NOT NULL AND [ResolvedBy] IS NOT NULL)");
        });
        b.HasKey(x => x.FlagId).HasName("PK_ApplicationFlags");
        b.Property(x => x.FlagId).UseIdentityColumn(1, 1);
        b.Property(x => x.Reason).HasMaxLength(30).IsRequired();
        b.Property(x => x.Note).HasMaxLength(500);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_ApplicationFlags_CreatedAt");
        b.Property(x => x.ResolvedBy).HasMaxLength(100);
        b.Property(x => x.ResolvedAt).HasColumnType("datetime2");
        b.Property(x => x.ResolutionNote).HasMaxLength(500);
        b.Property(x => x.UpdatedAt).HasColumnType("datetime2");
        b.Property(x => x.Version).IsConcurrencyToken();

        b.HasOne(x => x.Application).WithMany(a => a.Flags).HasForeignKey(x => x.ApplicationId)
            .HasConstraintName("FK_ApplicationFlags_Applications").OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ApplicationId, x.CreatedAt }).HasDatabaseName("IX_ApplicationFlags_Application_CreatedAt");
        // Tối đa MỘT cờ đang mở cho mỗi hồ sơ (hai HR bấm cùng lúc: người sau nhận 409).
        b.HasIndex(x => x.ApplicationId).IsUnique().HasFilter("[Status] = N'Open'").HasDatabaseName("UX_ApplicationFlags_OpenPerApplication");
    }
}

public class ApplicationFlagEventConfiguration : IEntityTypeConfiguration<ApplicationFlagEvent>
{
    private static readonly string Actions = string.Join(", ", ManualFlagActions.All.Select(r => $"N'{r}'"));

    public void Configure(EntityTypeBuilder<ApplicationFlagEvent> b)
    {
        b.ToTable("ApplicationFlagEvents", t =>
        {
            t.HasCheckConstraint("CK_ApplicationFlagEvents_Action", $"[Action] COLLATE Latin1_General_100_BIN2 IN ({Actions})");
            t.HasCheckConstraint("CK_ApplicationFlagEvents_Actor", "LEN(LTRIM(RTRIM([Actor]))) > 0");
        });
        b.HasKey(x => x.EventId).HasName("PK_ApplicationFlagEvents");
        b.Property(x => x.EventId).UseIdentityColumn(1, 1);
        b.Property(x => x.Action).HasMaxLength(20).IsRequired();
        b.Property(x => x.Actor).HasMaxLength(100).IsRequired();
        b.Property(x => x.Note).HasMaxLength(500);
        b.Property(x => x.CreatedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_ApplicationFlagEvents_CreatedAt");
        b.HasOne(x => x.Flag).WithMany(f => f.Events).HasForeignKey(x => x.FlagId)
            .HasConstraintName("FK_ApplicationFlagEvents_ApplicationFlags").OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.FlagId, x.CreatedAt }).HasDatabaseName("IX_ApplicationFlagEvents_Flag_CreatedAt");
    }
}
