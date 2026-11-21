using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

/// <summary>A51: liên kết đăng nhập ngoài. Provider không bị CHECK cứng vào danh sách nhà cung cấp; A58/A59 kiểm tra ở tầng ứng dụng.</summary>
public class UserExternalLoginConfiguration : IEntityTypeConfiguration<UserExternalLogin>
{
    public void Configure(EntityTypeBuilder<UserExternalLogin> b)
    {
        b.ToTable("UserExternalLogins");
        b.HasKey(x => x.UserExternalLoginId).HasName("PK_UserExternalLogins");
        b.Property(x => x.UserExternalLoginId).UseIdentityColumn(1, 1);
        b.Property(x => x.Provider).HasMaxLength(20).IsUnicode(false).IsRequired();
        b.Property(x => x.ProviderKey).HasMaxLength(200).IsRequired();
        b.Property(x => x.EmailAtLink).HasMaxLength(254);
        b.Property(x => x.LinkedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_UserExternalLogins_LinkedAt");
        b.Property(x => x.LastUsedAt).HasColumnType("datetime2");

        b.HasIndex(x => new { x.Provider, x.ProviderKey }).IsUnique().HasDatabaseName("UX_UserExternalLogins_Provider_Key");
        b.HasIndex(x => new { x.UserId, x.Provider }).IsUnique().HasDatabaseName("UX_UserExternalLogins_User_Provider");
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
            .HasConstraintName("FK_UserExternalLogins_Users").OnDelete(DeleteBehavior.Cascade);
    }
}
