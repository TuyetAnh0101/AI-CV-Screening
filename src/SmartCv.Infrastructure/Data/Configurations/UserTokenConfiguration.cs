using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

/// <summary>A51: token dùng một lần. Chỉ lưu SHA-256 (CHAR(64)); nghiệp vụ ở A56-A58.</summary>
public class UserTokenConfiguration : IEntityTypeConfiguration<UserToken>
{
    public void Configure(EntityTypeBuilder<UserToken> b)
    {
        b.ToTable("UserTokens", t => t.HasCheckConstraint("CK_UserTokens_TokenHash", "LEN([TokenHash]) = 64"));
        b.HasKey(x => x.UserTokenId).HasName("PK_UserTokens");
        b.Property(x => x.UserTokenId).UseIdentityColumn(1, 1);
        b.Property(x => x.Purpose).HasMaxLength(30).IsUnicode(false).IsRequired();
        b.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_UserTokens_CreatedAt");
        b.Property(x => x.ExpiresAt).HasColumnType("datetime2");
        b.Property(x => x.ConsumedAt).HasColumnType("datetime2");
        b.Property(x => x.Payload).HasMaxLength(500);

        b.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("UX_UserTokens_TokenHash");
        b.HasIndex(x => new { x.UserId, x.Purpose, x.CreatedAt }).HasDatabaseName("IX_UserTokens_User_Purpose_CreatedAt");
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
            .HasConstraintName("FK_UserTokens_Users").OnDelete(DeleteBehavior.Cascade);
    }
}
