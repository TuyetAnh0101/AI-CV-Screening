using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

public class EvaluationConfiguration : IEntityTypeConfiguration<Evaluation>
{
    public void Configure(EntityTypeBuilder<Evaluation> b)
    {
        b.ToTable("Evaluations", t =>
        {
            t.HasCheckConstraint("CK_Evaluations_KeywordScore", "KeywordScore BETWEEN 0 AND 100");
            t.HasCheckConstraint("CK_Evaluations_AiScore", "AiScore BETWEEN 0 AND 100");
            t.HasCheckConstraint("CK_Evaluations_FinalScore", "FinalScore BETWEEN 0 AND 100");
            t.HasCheckConstraint("CK_Evaluations_ScoreSource", "ScoreSource IN (N'Combined', N'KeywordOnly')");
            t.HasCheckConstraint("CK_Evaluations_YearsExperience", "YearsExperience >= 0");
            t.HasCheckConstraint("CK_Evaluations_Status", "Status IN (N'Pending', N'Completed', N'Failed')");
        });

        b.HasKey(x => x.EvaluationId).HasName("PK_Evaluations");
        b.Property(x => x.EvaluationId).UseIdentityColumn(1, 1);

        b.Property(x => x.KeywordScore).HasPrecision(5, 2);
        b.Property(x => x.AiScore).HasPrecision(5, 2);
        b.Property(x => x.FinalScore).HasPrecision(5, 2);
        b.Property(x => x.ScoreSource).HasMaxLength(20);
        b.Property(x => x.FitLabel).HasMaxLength(30);
        b.Property(x => x.YearsExperience).HasPrecision(4, 1);
        b.Property(x => x.Reasoning);       // NVARCHAR(MAX) NULL
        b.Property(x => x.AiRawResponse);   // NVARCHAR(MAX) NULL
        b.Property(x => x.Status).HasMaxLength(20).IsRequired()
            .HasDefaultValue("Pending", "DF_Evaluations_Status").ValueGeneratedNever();
        b.Property(x => x.ErrorMessage).HasMaxLength(1000);
        b.Property(x => x.CreatedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_Evaluations_CreatedAt");

        // Mỗi hồ sơ chỉ có một kết quả trong một lượt chấm
        b.HasAlternateKey(x => new { x.RunId, x.ApplicationId }).HasName("UQ_Evaluations_Run_Application");
        b.HasIndex(x => x.ApplicationId).HasDatabaseName("IX_Evaluations_ApplicationId");
        // Chỉ mục phục vụ bảng xếp hạng: RunId tăng dần, FinalScore giảm dần
        b.HasIndex(x => new { x.RunId, x.FinalScore })
            .IsDescending(false, true)
            .HasDatabaseName("IX_Evaluations_Run_FinalScore");

        b.HasOne(x => x.Run)
            .WithMany(r => r.Evaluations)
            .HasForeignKey(x => x.RunId)
            .HasConstraintName("FK_Evaluations_EvaluationRuns")
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.Application)
            .WithMany(a => a.Evaluations)
            .HasForeignKey(x => x.ApplicationId)
            .HasConstraintName("FK_Evaluations_Applications")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
