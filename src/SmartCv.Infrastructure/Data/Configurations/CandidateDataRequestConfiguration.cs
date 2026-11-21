using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

/// <summary>A43: bảng CandidateDataRequests (nhật ký yêu cầu xem / xóa dữ liệu của ứng viên).</summary>
public class CandidateDataRequestConfiguration : IEntityTypeConfiguration<CandidateDataRequest>
{
    public void Configure(EntityTypeBuilder<CandidateDataRequest> b)
    {
        b.ToTable("CandidateDataRequests", t =>
            t.HasCheckConstraint("CK_CandidateDataRequests_RequestType", "[RequestType] COLLATE Latin1_General_100_BIN2 IN (N'Export', N'Erase')"));

        b.HasKey(x => x.RequestId).HasName("PK_CandidateDataRequests");
        b.Property(x => x.WorkspaceId).IsRequired();
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => new { x.WorkspaceId, x.CandidateId, x.CreatedAt }).HasDatabaseName("IX_CandidateDataRequests_Workspace_Candidate_CreatedAt");
        b.Property(x => x.RequestId).UseIdentityColumn(1, 1);
        b.Property(x => x.RequestType).HasMaxLength(10).IsRequired();
        b.Property(x => x.HandledBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.Note).HasMaxLength(500);
        b.Property(x => x.ResultSummary).HasMaxLength(500);
        b.Property(x => x.CvIds).HasColumnType("nvarchar(max)");
        b.Property(x => x.CreatedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_CandidateDataRequests_CreatedAt");

        // Không khai báo quan hệ tới Candidates: nhật ký phải còn sau khi ứng viên bị xóa.
        b.HasIndex(x => new { x.CandidateId, x.CreatedAt }).HasDatabaseName("IX_CandidateDataRequests_Candidate_CreatedAt");
    }
}
