using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Data.Configurations;

public class EvaluationRunConfiguration : IEntityTypeConfiguration<EvaluationRun>
{
    public void Configure(EntityTypeBuilder<EvaluationRun> b)
    {
        b.ToTable("EvaluationRuns", t =>
        {
            t.HasCheckConstraint("CK_EvaluationRuns_RunType", "RunType IN (N'Screening', N'PromptTest')");
            t.HasCheckConstraint("CK_EvaluationRuns_Status", "Status IN (N'Queued', N'Running', N'Completed', N'Failed')");
            t.HasCheckConstraint("CK_EvaluationRuns_Temperature", "Temperature >= 0");
            t.HasCheckConstraint("CK_EvaluationRuns_KeywordWeight", "KeywordWeight BETWEEN 0 AND 1");
            t.HasCheckConstraint("CK_EvaluationRuns_AiWeight", "AiWeight BETWEEN 0 AND 1");
            t.HasCheckConstraint("CK_EvaluationRuns_Weights", "KeywordWeight + AiWeight = 1");
            t.HasCheckConstraint("CK_EvaluationRuns_PassThreshold", "PassThreshold BETWEEN 0 AND 100");
            t.HasCheckConstraint("CK_EvaluationRuns_TotalItems", "TotalItems >= 0");
            t.HasCheckConstraint("CK_EvaluationRuns_ProcessedItems", "ProcessedItems >= 0");
            t.HasCheckConstraint("CK_EvaluationRuns_FailedItems", "FailedItems >= 0");
            t.HasCheckConstraint("CK_EvaluationRuns_AccuracyPercent", "AccuracyPercent BETWEEN 0 AND 100");
            t.HasCheckConstraint("CK_EvaluationRuns_Set",
                "(RunType = N'PromptTest' AND EvaluationSetId IS NOT NULL) OR (RunType = N'Screening' AND EvaluationSetId IS NULL)");
        });

        b.HasKey(x => x.RunId).HasName("PK_EvaluationRuns");
        b.Property(x => x.RunId).UseIdentityColumn(1, 1);

        b.Property(x => x.RunType).HasMaxLength(20).IsRequired();
        b.Property(x => x.ModelName).HasMaxLength(100).IsRequired();
        b.Property(x => x.Temperature).HasPrecision(3, 2);
        b.Property(x => x.KeywordWeight).HasPrecision(3, 2);
        b.Property(x => x.AiWeight).HasPrecision(3, 2);
        b.Property(x => x.PassThreshold).HasPrecision(5, 2)
            .HasDefaultValue(70m, "DF_EvaluationRuns_PassThreshold").ValueGeneratedNever();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired()
            .HasDefaultValue("Queued", "DF_EvaluationRuns_Status").ValueGeneratedNever();
        b.Property(x => x.ProcessedItems).HasDefaultValue(0, "DF_EvaluationRuns_ProcessedItems").ValueGeneratedNever();
        b.Property(x => x.FailedItems).HasDefaultValue(0, "DF_EvaluationRuns_FailedItems").ValueGeneratedNever();
        b.Property(x => x.AccuracyPercent).HasPrecision(5, 2);
        b.Property(x => x.CreatedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()", "DF_EvaluationRuns_CreatedAt");
        b.Property(x => x.CompletedAt).HasColumnType("datetime2");

        b.HasOne(x => x.PromptVersion)
            .WithMany(p => p.EvaluationRuns)
            .HasForeignKey(x => x.PromptVersionId)
            .HasConstraintName("FK_EvaluationRuns_PromptVersions")
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.EvaluationSet)
            .WithMany(s => s.EvaluationRuns)
            .HasForeignKey(x => x.EvaluationSetId)
            .IsRequired(false)
            .HasConstraintName("FK_EvaluationRuns_EvaluationSets")
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.PromptVersionId).HasDatabaseName("IX_EvaluationRuns_PromptVersionId");
        b.HasIndex(x => x.EvaluationSetId).HasDatabaseName("IX_EvaluationRuns_EvaluationSetId");
    }
}
