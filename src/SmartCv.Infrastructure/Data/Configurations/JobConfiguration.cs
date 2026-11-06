using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data.Seed;

namespace SmartCv.Infrastructure.Data.Configurations;

public class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> b)
    {
        b.ToTable("Jobs", t =>
        {
            t.HasCheckConstraint("CK_Jobs_MinGpa", "MinGpa BETWEEN 0 AND 4");
            t.HasCheckConstraint("CK_Jobs_MinYearsExperience", "MinYearsExperience >= 0");
        });

        b.HasKey(x => x.JobId).HasName("PK_Jobs");
        b.Property(x => x.JobId).UseIdentityColumn(1, 1);

        b.Property(x => x.Title).HasMaxLength(150).IsRequired();
        b.HasAlternateKey(x => x.Title).HasName("UQ_Jobs_Title");

        b.Property(x => x.Description);                    // NVARCHAR(MAX) NULL
        b.Property(x => x.MinGpa).HasPrecision(3, 2);

        // Có DEFAULT trong DB, nhưng EF luôn gửi giá trị từ C# (ValueGeneratedNever)
        // để tránh lỗi: gán false/0 bị EF bỏ qua và DB lại điền giá trị mặc định.
        b.Property(x => x.MinYearsExperience).HasPrecision(4, 1)
            .HasDefaultValue(0m, "DF_Jobs_MinYearsExperience").ValueGeneratedNever();
        b.Property(x => x.IsActive)
            .HasDefaultValue(true, "DF_Jobs_IsActive").ValueGeneratedNever();

        b.Property(x => x.CreatedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_Jobs_CreatedAt");

        // A3: dữ liệu nền, nạp qua migration SeedJobs
        b.HasData(JobSeedData.Jobs);
    }
}
