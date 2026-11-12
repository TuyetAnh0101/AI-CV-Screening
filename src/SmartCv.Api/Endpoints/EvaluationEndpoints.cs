using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;
using Microsoft.Extensions.Options;
using SmartCv.Core.Constants;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// A13: chi tiết kết quả chấm một CV (trang B9):
///   GET /api/evaluations/{id}
/// Gồm: điểm keyword / AI / cuối, nguồn điểm, mức, lý do, kỹ năng đạt/thiếu kèm bằng chứng,
/// hạng trong job, và phiên bản prompt + cấu hình đã dùng (A12).
/// </summary>
public static class EvaluationEndpoints
{
    public static WebApplication MapEvaluationEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() ||
            !app.Services.GetRequiredService<IOptions<CvUploadEndpointOptions>>().Value.Enabled) return app;
        app.MapGet("/api/evaluations/{id:int}", GetAsync)
            .WithTags("Evaluations")
            .WithName("GetEvaluation")
            .WithSummary("Chi tiết kết quả chấm một CV");
        return app;
    }

    private static async Task<Results<Ok<EvaluationDetailDto>, NotFound>> GetAsync(int id, SmartCvDbContext db,
        IOptions<ReviewFlagOptions> flagOptions, CancellationToken ct)
    {
        var e = await db.Evaluations.AsNoTracking()
            .Where(x => x.EvaluationId == id)
            .Select(x => new
            {
                x.EvaluationId, x.Status, x.ErrorMessage, x.RunId,
                x.Run.RunType, x.Run.PromptVersionId, PromptVersionName = x.Run.PromptVersion.VersionName,
                x.Run.ModelName, x.Run.Temperature, x.Run.KeywordWeight, x.Run.AiWeight, x.Run.PassThreshold,
                x.ApplicationId, x.Application.CvId, FileName = x.Application.Cv.OriginalFileName,
                x.Application.Stage, x.Application.RejectReason, x.Application.StageUpdatedAt, x.Application.StageVersion,
                x.Application.Cv.ContentType, x.Application.Cv.ExtractionMethod,
                CandidateName = x.Application.Cv.Candidate != null ? x.Application.Cv.Candidate.FullName : null,
                CandidateEmail = x.Application.Cv.Candidate != null ? x.Application.Cv.Candidate.Email : null,
                x.Application.JobId, JobTitle = x.Application.Job.Title,
                JobMinGpa = x.Application.Job.MinGpa, JobMinYears = x.Application.Job.MinYearsExperience,
                x.KeywordScore, x.AiScore, x.FinalScore, x.ScoreSource, x.FitLabel, x.PredictedPass,
                x.YearsExperience, x.Reasoning, x.AiRawResponse, x.CreatedAt
            })
            .FirstOrDefaultAsync(ct);
        if (e is null) return TypedResults.NotFound();

        // Kỹ năng: lấy TẤT CẢ kỹ năng của job, ghép kết quả đã lưu (bắt buộc trước, đạt trước)
        var saved = await db.EvaluationSkills.AsNoTracking()
            .Where(s => s.EvaluationId == id)
            .ToDictionaryAsync(s => s.SkillCode, ct);
        var jobSkills = await db.JobSkills.AsNoTracking()
            .Where(s => s.JobId == e.JobId)
            .Select(s => new { s.SkillCode, s.DisplayName, s.SkillType })
            .ToListAsync(ct);

        var skills = jobSkills
            .Select(s => new EvaluationSkillDto
            {
                SkillCode = s.SkillCode,
                DisplayName = s.DisplayName,
                SkillType = s.SkillType,
                IsMatched = saved.TryGetValue(s.SkillCode, out var m) && m.IsMatched,
                Evidence = saved.TryGetValue(s.SkillCode, out var m2) ? m2.Evidence : null
            })
            .OrderBy(s => s.SkillType == "MustHave" ? 0 : 1).ThenByDescending(s => s.IsMatched).ThenBy(s => s.DisplayName)
            .ToList();

        // Hạng trong job của lượt chấm (cùng cách tính với bảng xếp hạng)
        int? rank = null;
        var total = await db.Evaluations.CountAsync(x => x.RunId == e.RunId && x.Application.JobId == e.JobId && x.Status == EvaluationStatuses.Completed && x.FinalScore != null, ct);
        if (e.Status == EvaluationStatuses.Completed && e.FinalScore is not null)
            rank = 1 + await db.Evaluations.CountAsync(x => x.RunId == e.RunId && x.Application.JobId == e.JobId && x.Status == EvaluationStatuses.Completed && x.FinalScore > e.FinalScore, ct);

        var review = (await EvaluationReviewReader.ReadAsync(db.Evaluations.Where(x => x.EvaluationId == id), flagOptions.Value, ct))[id];
        return TypedResults.Ok(new EvaluationDetailDto
        {
            ReviewFlags = review.Flags,
            ExtractedTextLength = review.TextLength,
            EvaluationId = e.EvaluationId,
            Status = e.Status,
            ErrorMessage = e.ErrorMessage,
            RunId = e.RunId,
            RunType = e.RunType,
            PromptVersionId = e.PromptVersionId,
            PromptVersionName = e.PromptVersionName,
            ModelName = e.ModelName,
            Temperature = e.Temperature,
            KeywordWeight = e.KeywordWeight,
            AiWeight = e.AiWeight,
            PassThreshold = e.PassThreshold,
            ApplicationId = e.ApplicationId,
            Stage = e.Stage,
            RejectReason = e.RejectReason,
            StageUpdatedAt = e.StageUpdatedAt is { } su ? DateTime.SpecifyKind(su, DateTimeKind.Utc) : null,
            StageVersion = e.StageVersion,
            CvId = e.CvId,
            FileName = e.FileName,
            ContentType = e.ContentType,
            CandidateName = e.CandidateName,
            CandidateEmail = e.CandidateEmail,
            ExtractionMethod = e.ExtractionMethod,
            JobId = e.JobId,
            JobTitle = e.JobTitle,
            JobMinGpa = e.JobMinGpa,
            JobMinYearsExperience = e.JobMinYears,
            Rank = rank,
            RankTotal = total,
            KeywordScore = e.KeywordScore,
            AiScore = e.AiScore,
            FinalScore = e.FinalScore,
            ScoreSource = e.ScoreSource,
            FitLabel = e.FitLabel,
            PredictedPass = e.PredictedPass,
            YearsExperience = e.YearsExperience,
            Gpa = ReadGpa(e.AiRawResponse),
            Reasoning = e.Reasoning,
            AiRawResponse = e.AiRawResponse,
            CreatedAt = DateTime.SpecifyKind(e.CreatedAt, DateTimeKind.Utc),
            Skills = skills
        });
    }
    private static decimal? ReadGpa(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("gpa", out var gpa)
                && gpa.ValueKind == JsonValueKind.Number && gpa.TryGetDecimal(out var value) ? value : null;
        }
        catch (JsonException) { return null; }
    }
}
