using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

public class CvConfiguration : IEntityTypeConfiguration<Cv>
{
    public void Configure(EntityTypeBuilder<Cv> b)
    {
        b.ToTable("Cvs", t =>
        {
            t.HasCheckConstraint("CK_Cvs_FileSizeBytes", "FileSizeBytes > 0");
            t.HasCheckConstraint("CK_Cvs_ExtractionMethod", "ExtractionMethod IN (N'PdfText', N'PdfOcr', N'ImageOcr')");
            t.HasCheckConstraint("CK_Cvs_ExtractionStatus", "ExtractionStatus IN (N'Pending', N'Completed', N'Failed')");
        });

        b.HasKey(x => x.CvId).HasName("PK_Cvs");
        // GUID do code tự tạo (Guid.NewGuid()) vì nó cũng là tên file lưu trên ổ đĩa
        b.Property(x => x.CvId).ValueGeneratedNever();

        b.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
        b.Property(x => x.StoredPath).HasMaxLength(500).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        // CHAR(64) cho mã SHA-256
        b.Property(x => x.FileHash).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        b.HasAlternateKey(x => x.FileHash).HasName("UQ_Cvs_FileHash");

        b.Property(x => x.ExtractedText);                     // NVARCHAR(MAX) NULL
        b.Property(x => x.ExtractionMethod).HasMaxLength(20);
        b.Property(x => x.ExtractionStatus).HasMaxLength(20).IsRequired()
            .HasDefaultValue("Pending", "DF_Cvs_ExtractionStatus").ValueGeneratedNever();
        b.Property(x => x.ExtractionError).HasMaxLength(1000);
        b.Property(x => x.UploadedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_Cvs_UploadedAt");

        b.HasOne(x => x.Candidate)
            .WithMany(c => c.Cvs)
            .HasForeignKey(x => x.CandidateId)
            .IsRequired(false)
            .HasConstraintName("FK_Cvs_Candidates")
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.CandidateId).HasDatabaseName("IX_Cvs_CandidateId");
    }
}
