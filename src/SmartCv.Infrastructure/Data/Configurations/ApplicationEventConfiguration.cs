using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

/// <summary>A35: bảng ApplicationEvents (lịch sử quyết định, chỉ thêm).</summary>
public class ApplicationEventConfiguration : IEntityTypeConfiguration<ApplicationEvent>
{
    public const string NoChangeTrigger = "TR_ApplicationEvents_NoChange";

    private static readonly string StageList = string.Join(", ", ApplicationStages.All.Select(s => $"N'{s}'"));
    private static readonly string ReasonList = string.Join(", ", RejectReasons.All.Select(s => $"N'{s}'"));

    public void Configure(EntityTypeBuilder<ApplicationEvent> b)
    {
        b.ToTable("ApplicationEvents", t =>
        {
            t.HasCheckConstraint("CK_ApplicationEvents_FromStage", $"[FromStage] COLLATE Latin1_General_100_BIN2 IN ({StageList})");
            t.HasCheckConstraint("CK_ApplicationEvents_ToStage", $"[ToStage] COLLATE Latin1_General_100_BIN2 IN ({StageList})");
            t.HasCheckConstraint("CK_ApplicationEvents_RejectReason", $"[RejectReason] IS NULL OR [RejectReason] COLLATE Latin1_General_100_BIN2 IN ({ReasonList})");
            t.HasCheckConstraint("CK_ApplicationEvents_RejectReason_Stage",
                "([ToStage] = N'Rejected' AND [RejectReason] IS NOT NULL) OR ([ToStage] <> N'Rejected' AND [RejectReason] IS NULL)");
            // Bảng có trigger chặn sửa / xóa (tạo trong migration). Khai báo để EF không dùng OUTPUT khi INSERT.
            t.HasTrigger(NoChangeTrigger);
            t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_ApplicationEvents_Actor", "LEN(LTRIM(RTRIM([Actor]))) > 0");
        });

        b.HasKey(x => x.EventId).HasName("PK_ApplicationEvents");
        b.Property(x => x.EventId).UseIdentityColumn(1, 1);
        b.Property(x => x.FromStage).HasMaxLength(20).IsRequired();
        b.Property(x => x.ToStage).HasMaxLength(20).IsRequired();
        b.Property(x => x.RejectReason).HasMaxLength(30);
        b.Property(x => x.Note).HasMaxLength(500);
        b.Property(x => x.Actor).HasMaxLength(100).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()");

        // Lịch sử một hồ sơ, mới nhất trước
        b.HasIndex(x => new { x.ApplicationId, x.CreatedAt }).IsDescending(false, true)
            .HasDatabaseName("IX_ApplicationEvents_Application_CreatedAt");

        b.HasOne(x => x.Application)
            .WithMany(a => a.Events)
            .HasForeignKey(x => x.ApplicationId)
            .HasConstraintName("FK_ApplicationEvents_Applications")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
