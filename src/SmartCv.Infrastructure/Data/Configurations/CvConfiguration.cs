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
            t.HasCheckConstraint("CK_Cvs_SourceChannel", "SourceChannel IS NULL OR (SourceChannel COLLATE Latin1_General_100_BIN2 IN (N'Email', N'Website', N'JobBoard', N'Referral', N'Other') AND DATALENGTH(SourceChannel) = DATALENGTH(RTRIM(SourceChannel)))");
        });

        b.HasKey(x => x.CvId).HasName("PK_Cvs");
        // GUID do code tự tạo (Guid.NewGuid()) vì nó cũng là tên file lưu trên ổ đĩa
        b.Property(x => x.CvId).ValueGeneratedNever();

        b.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
        b.Property(x => x.StoredPath).HasMaxLength(500).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        // CHAR(64) cho mã SHA-256
        b.Property(x => x.FileHash).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        // A51: SHA-256 chỉ duy nhất trong workspace (thay UQ_Cvs_FileHash toàn cục). Giữ UNIQUE để hai lần tải
        // đồng thời cùng một file vẫn chỉ cho một bản vào database (CvUploadService dựa vào lỗi 2601/2627).
        b.Property(x => x.WorkspaceId).IsRequired();
        b.HasIndex(x => new { x.WorkspaceId, x.FileHash }).IsUnique().HasDatabaseName("IX_Cvs_Workspace_FileHash");
        b.HasAlternateKey(x => new { x.WorkspaceId, x.CvId }).HasName("AK_Cvs_Workspace_Cv");
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId)
            .HasConstraintName("FK_Cvs_Workspaces").OnDelete(DeleteBehavior.NoAction);

        b.Property(x => x.ExtractedText);                     // NVARCHAR(MAX) NULL
        b.Property(x => x.ExtractionMethod).HasMaxLength(20);
        b.Property(x => x.ExtractionStatus).HasMaxLength(20).IsRequired()
            .HasDefaultValue("Pending", "DF_Cvs_ExtractionStatus").ValueGeneratedNever();
        b.Property(x => x.ExtractionError).HasMaxLength(1000);
        b.Property(x => x.SourceChannel).HasMaxLength(30);
        b.Property(x => x.ConsentConfirmedAt).HasColumnType("datetime2");
        b.Property(x => x.RetainUntil).HasColumnType("datetime2");
        b.Property(x => x.AnonymizedAt).HasColumnType("datetime2");
        b.HasIndex(x => x.RetainUntil).HasDatabaseName("IX_Cvs_RetainUntil")
            .HasFilter("[AnonymizedAt] IS NULL AND [RetainUntil] IS NOT NULL");
        b.Property(x => x.UploadedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_Cvs_UploadedAt");

        b.HasOne(x => x.Candidate)
            .WithMany(c => c.Cvs)
            .HasForeignKey(x => new { x.WorkspaceId, x.CandidateId })
            .HasPrincipalKey(c => new { c.WorkspaceId, c.CandidateId })
            .IsRequired(false)
            .HasConstraintName("FK_Cvs_Candidates")
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.CandidateId).HasDatabaseName("IX_Cvs_CandidateId");
    }
}
