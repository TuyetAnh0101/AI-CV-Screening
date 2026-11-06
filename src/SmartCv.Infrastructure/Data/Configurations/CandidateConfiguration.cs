using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

public class CandidateConfiguration : IEntityTypeConfiguration<Candidate>
{
    public void Configure(EntityTypeBuilder<Candidate> b)
    {
        b.ToTable("Candidates", t =>
        {
            t.HasCheckConstraint("CK_Candidates_PhoneNumber", "PhoneNumber NOT LIKE '%[^0-9]%'");
        });

        b.HasKey(x => x.CandidateId).HasName("PK_Candidates");
        b.Property(x => x.CandidateId).UseIdentityColumn(1, 1);

        b.Property(x => x.FullName).HasMaxLength(150).IsRequired();
        b.Property(x => x.Email).HasMaxLength(254);
        // CHAR(10): độ dài cố định, không Unicode
        b.Property(x => x.PhoneNumber).HasMaxLength(10).IsFixedLength().IsUnicode(false);
        b.Property(x => x.CreatedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_Candidates_CreatedAt");
    }
}
