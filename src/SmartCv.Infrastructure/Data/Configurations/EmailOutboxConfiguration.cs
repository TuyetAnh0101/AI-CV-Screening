using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

public sealed class EmailOutboxConfiguration : IEntityTypeConfiguration<EmailOutboxItem>
{
    public void Configure(EntityTypeBuilder<EmailOutboxItem> b)
    {
        b.ToTable("EmailOutbox", t =>
        {
            t.HasCheckConstraint("CK_EmailOutbox_Status", "[Status] IN ('Pending','Sent','Failed')");
            t.HasCheckConstraint("CK_EmailOutbox_Attempts", "[Attempts] BETWEEN 0 AND 3");
            t.HasCheckConstraint("CK_EmailOutbox_Claim", "([ClaimId] IS NULL AND [LeaseUntilUtc] IS NULL) OR ([ClaimId] IS NOT NULL AND [LeaseUntilUtc] IS NOT NULL)");
            t.HasCheckConstraint("CK_EmailOutbox_Payload", "([Status] = 'Pending' AND [ProtectedPayload] IS NOT NULL) OR ([Status] <> 'Pending' AND [ProtectedPayload] IS NULL)");
        });
        b.HasKey(x => x.EmailOutboxId);
        b.Property(x => x.EmailOutboxId).UseIdentityColumn(1,1);
        b.Property(x => x.ToAddress).HasMaxLength(320).IsRequired();
        b.Property(x => x.Template).HasMaxLength(50).IsUnicode(false).IsRequired();
        b.Property(x => x.ProtectedPayload).HasColumnType("nvarchar(max)");
        b.Property(x => x.Status).HasMaxLength(10).IsUnicode(false).IsRequired();
        b.Property(x => x.Attempts).HasColumnType("tinyint").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnType("datetime2");
        b.Property(x => x.NextAttemptAt).HasColumnType("datetime2");
        b.Property(x => x.SentAt).HasColumnType("datetime2");
        b.Property(x => x.LastError).HasMaxLength(100).IsUnicode(false);
        b.Property(x => x.LeaseUntilUtc).HasColumnType("datetime2");
        b.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("IX_EmailOutbox_Status_NextAttemptAt");
        b.HasIndex(x => new { x.UserId, x.Template, x.CreatedAt }).HasDatabaseName("IX_EmailOutbox_User_Template_Created");
        b.HasIndex(x => new { x.ClaimId, x.EmailOutboxId }).HasDatabaseName("IX_EmailOutbox_Claim");
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.SetNull).HasConstraintName("FK_EmailOutbox_Users");
    }
}
