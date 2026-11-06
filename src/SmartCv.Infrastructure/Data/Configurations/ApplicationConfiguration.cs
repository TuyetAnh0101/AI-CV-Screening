using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

public class ApplicationConfiguration : IEntityTypeConfiguration<Application>
{
    public void Configure(EntityTypeBuilder<Application> b)
    {
        b.ToTable("Applications");

        b.HasKey(x => x.ApplicationId).HasName("PK_Applications");
        b.Property(x => x.ApplicationId).UseIdentityColumn(1, 1);
        b.Property(x => x.CreatedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_Applications_CreatedAt");

        // Một CV chỉ ứng tuyển một job một lần
        b.HasAlternateKey(x => new { x.CvId, x.JobId }).HasName("UQ_Applications_Cv_Job");
        b.HasIndex(x => x.JobId).HasDatabaseName("IX_Applications_JobId");

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
