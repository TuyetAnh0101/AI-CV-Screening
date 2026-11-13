using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Contracts;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Endpoints;

public static partial class RunEndpoints
{
    private static async Task<IResult> CompareAsync(int id, string? evaluationIds, SmartCvDbContext db,
        IOptions<ReviewFlagOptions> flagOptions, CancellationToken ct)
    {
        // Do not remove empty entries: "1,,2" and "1,2," are malformed requests.
        if (string.IsNullOrWhiteSpace(evaluationIds) || evaluationIds.Length > 128)
            return CompareProblem(400, "Mã hồ sơ không hợp lệ", "Chọn từ 2 đến 3 mã kết quả đánh giá nguyên dương, không trùng.");
        var parts = evaluationIds.Split(',', StringSplitOptions.TrimEntries);
        var ids = new List<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
                return CompareProblem(400, "Mã hồ sơ không hợp lệ", "Mỗi mã kết quả đánh giá phải là số nguyên dương.");
            ids.Add(value);
        }
        if (ids.Count is < 2 or > 3 || ids.Distinct().Count() != ids.Count)
            return CompareProblem(400, "Số hồ sơ không hợp lệ", "Chọn từ 2 đến 3 kết quả đánh giá khác nhau.");

        if (!await db.EvaluationRuns.AsNoTracking().AnyAsync(r => r.RunId == id, ct))
            return CompareProblem(404, "Không tìm thấy lượt chấm", "Lượt chấm được yêu cầu không tồn tại.");
        var selected = await db.Evaluations.AsNoTracking().Where(e => ids.Contains(e.EvaluationId))
            .Select(e => new { e.EvaluationId, e.RunId, e.Application.JobId,
                Reasoning = e.Application.Cv.AnonymizedAt == null ? e.Reasoning : null }).ToListAsync(ct);
        if (selected.Count != ids.Count)
            return CompareProblem(404, "Không tìm thấy kết quả đánh giá", "Một hoặc nhiều kết quả đánh giá không tồn tại.");
        if (selected.Any(e => e.RunId != id))
            return CompareProblem(400, "Khác lượt chấm", "Các kết quả đánh giá phải thuộc lượt chấm đang so sánh.");
        if (selected.Select(e => e.JobId).Distinct().Count() != 1)
            return CompareProblem(400, "Khác vị trí tuyển dụng", "Chỉ so sánh các hồ sơ ứng tuyển cùng một vị trí tuyển dụng.");

        var jobId = selected[0].JobId;
        // Reuse Ranking's saved scores, competition ranks and review projection. No rescoring.
        var result = await RankingAsync(id, db, flagOptions, ct, jobId);
        if (result.Result is not Ok<RankingResponse> { Value: { } ranking })
            return CompareProblem(404, "Không tìm thấy dữ liệu", "Dữ liệu lượt chấm không còn sẵn sàng. Hãy tải lại.");
        var job = await db.Jobs.AsNoTracking().Where(j => j.JobId == jobId)
            .Select(j => new { j.Title, j.MinGpa, j.MinYearsExperience }).SingleAsync(ct);
        var skills = await db.JobSkills.AsNoTracking().Where(s => s.JobId == jobId)
            .OrderBy(s => s.SkillType == "MustHave" ? 0 : 1).ThenBy(s => s.DisplayName).ThenBy(s => s.SkillCode)
            .Select(s => new CompareSkillDto { SkillCode = s.SkillCode, DisplayName = s.DisplayName, SkillType = s.SkillType }).ToListAsync(ct);
        var matches = await db.EvaluationSkills.AsNoTracking()
            .Where(s => ids.Contains(s.EvaluationId) && s.IsMatched && s.JobId == jobId)
            .Select(s => new { s.EvaluationId, s.SkillCode,
                Evidence = s.Evaluation.Application.Cv.AnonymizedAt == null ? s.Evidence : null }).ToListAsync(ct);
        var rows = ranking.Items.ToDictionary(e => e.EvaluationId);
        if (ids.Any(eid => !rows.ContainsKey(eid)))
            return CompareProblem(404, "Không tìm thấy dữ liệu", "Kết quả đánh giá không còn sẵn sàng. Hãy tải lại.");
        var reasoning = selected.ToDictionary(e => e.EvaluationId, e => e.Reasoning);
        var rankTotal = ranking.Items.Count(e => e.Rank is not null);
        return Results.Ok(new CandidateCompareResponse
        {
            RunId = id, JobId = jobId, JobTitle = job.Title, JobMinGpa = job.MinGpa,
            JobMinYearsExperience = job.MinYearsExperience, Skills = skills,
            PromptVersionName = ranking.PromptVersionName, PassThreshold = ranking.PassThreshold,
            Candidates = ids.Select(eid =>
            {
                var row = rows[eid];
                return new CandidateCompareItemDto
                {
                    EvaluationId = eid, ApplicationId = row.ApplicationId, CvId = row.CvId,
                    CandidateName = row.CandidateName, FileName = row.FileName, Status = row.Status,
                    ErrorMessage = row.ErrorMessage, Rank = row.Rank, RankTotal = rankTotal,
                    KeywordScore = row.KeywordScore, AiScore = row.AiScore, FinalScore = row.FinalScore,
                    ScoreSource = row.ScoreSource, FitLabel = row.FitLabel, PredictedPass = row.PredictedPass,
                    YearsExperience = row.YearsExperience, Stage = row.Stage, RejectReason = row.RejectReason,
                    StageVersion = row.StageVersion, StageUpdatedAt = row.StageUpdatedAt,
                    Reasoning = reasoning[eid], MatchedMustHave = row.MatchedMustHave, TotalMustHave = row.TotalMustHave,
                    ReviewFlags = row.ReviewFlags,
                    MatchedSkills = matches.Where(s => s.EvaluationId == eid).ToDictionary(s => s.SkillCode, s => s.Evidence)
                };
            }).ToList()
        });
    }

    private static IResult CompareProblem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}
