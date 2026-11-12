using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;
using SmartCv.Core.Constants;

namespace SmartCv.Infrastructure.Data.Configurations;

public class ApplicationConfiguration : IEntityTypeConfiguration<Application>
{
    public void Configure(EntityTypeBuilder<Application> b)
    {
        var stages = string.Join(", ", ApplicationStages.All.Select(s => $"N'{s}'"));
        var reasons = string.Join(", ", RejectReasons.All.Select(s => $"N'{s}'"));
        b.ToTable("Applications", t =>
        {
            t.HasCheckConstraint("CK_Applications_Stage", $"[Stage] COLLATE Latin1_General_100_BIN2 IN ({stages})");
            t.HasCheckConstraint("CK_Applications_RejectReason", $"[RejectReason] IS NULL OR [RejectReason] COLLATE Latin1_General_100_BIN2 IN ({reasons})");
            t.HasCheckConstraint("CK_Applications_RejectReason_Stage",
                "([Stage] = N'Rejected' AND [RejectReason] IS NOT NULL) OR ([Stage] <> N'Rejected' AND [RejectReason] IS NULL)");
        });

        b.HasKey(x => x.ApplicationId).HasName("PK_Applications");
        b.Property(x => x.ApplicationId).UseIdentityColumn(1, 1);
        b.Property(x => x.CreatedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_Applications_CreatedAt");
        b.Property(x => x.Stage).HasMaxLength(20).IsRequired().HasDefaultValue(ApplicationStages.New).IsConcurrencyToken();
        b.Property(x => x.RejectReason).HasMaxLength(30).IsConcurrencyToken();
        b.Property(x => x.StageUpdatedAt).HasColumnType("datetime2");
        b.Property(x => x.StageVersion).IsConcurrencyToken().HasDefaultValueSql("NEWID()");

        // Một CV chỉ ứng tuyển một job một lần
        b.HasAlternateKey(x => new { x.CvId, x.JobId }).HasName("UQ_Applications_Cv_Job");
        b.HasIndex(x => x.JobId).HasDatabaseName("IX_Applications_JobId");
        b.HasIndex(x => new { x.JobId, x.Stage }).HasDatabaseName("IX_Applications_Job_Stage");

        b.HasOne(x => x.Cv)
            .WithMany(c => c.Applications)
            .HasForeignKey(x => x.CvId)
            .HasConstraintName("FK_Applications_Cvs")
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.Job)
            .WithMany(j => j.Applications)
            .HasForeignKey(x => x.JobId)
            .HasConstraintName("FK_Applications_Jobs")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
