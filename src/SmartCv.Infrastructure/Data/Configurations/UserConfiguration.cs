using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        // BIN2 và độ dài byte giữ đúng hai giá trị, kể cả với catalog có collation không phân biệt hoa/thường.
        b.ToTable("Users", t => t.HasCheckConstraint("CK_Users_Role",
            "[Role] COLLATE Latin1_General_100_BIN2 IN (N'Admin', N'HR') AND DATALENGTH([Role]) = CASE WHEN [Role] = N'Admin' THEN 10 ELSE 4 END"));
        b.HasKey(x => x.UserId).HasName("PK_Users");
        b.Property(x => x.UserId).UseIdentityColumn(1, 1);
        b.Property(x => x.Email).HasMaxLength(254).IsRequired();
        b.HasIndex(x => x.Email).IsUnique().HasDatabaseName("UQ_Users_Email");
        b.Property(x => x.FullName).HasMaxLength(150).IsRequired();
        b.Property(x => x.PasswordHash).HasMaxLength(500).IsUnicode(false).IsRequired();
        b.Property(x => x.Role).HasMaxLength(20).IsRequired();
        b.Property(x => x.IsActive).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()");
        b.Property(x => x.LastLoginAt).HasColumnType("datetime2");
        b.Property(x => x.MustChangePassword).IsRequired();
        b.Property(x => x.SecurityStamp).HasMaxLength(64).IsUnicode(false).IsRequired();
        b.Property(x => x.EmailVerifiedAt).HasColumnType("datetime2");
        b.Property(x => x.SignupSource).HasMaxLength(20).IsUnicode(false).IsRequired()
            .HasDefaultValue(SignupSources.Admin, "DF_Users_SignupSource").ValueGeneratedNever();
        b.Property(x => x.TermsAcceptedAt).HasColumnType("datetime2");
        b.Ignore(x => x.HasPassword);
        b.ToTable(t => t.HasCheckConstraint("CK_Users_SignupSource",
            $"[SignupSource] COLLATE Latin1_General_100_BIN2 IN ({string.Join(", ", SignupSources.All.Select(s => $"N'{s}'"))})"));
    }
}
