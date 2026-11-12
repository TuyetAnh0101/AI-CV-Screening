using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Scoring;

namespace SmartCv.Api.Endpoints;

/// <summary>One scalar query shared by Ranking, Detail and Compare; never materializes CV text.</summary>
internal static class EvaluationReviewReader
{
    internal sealed record Snapshot(int? TextLength, List<ReviewFlagDto> Flags);

    internal static async Task<Dictionary<int, Snapshot>> ReadAsync(IQueryable<Evaluation> query,
        ReviewFlagOptions options, CancellationToken ct)
    {
        var rows = await query.AsNoTracking().Select(e => new
        {
            e.EvaluationId, e.Status, e.KeywordScore, e.AiScore, e.FinalScore, e.ScoreSource, e.Reasoning,
            e.Run.KeywordWeight, e.Run.AiWeight,
            // SQL Server LEN ignores trailing spaces. A sentinel preserves those characters.
            TextLength = e.Application.Cv.ExtractedText == null ? (int?)null : (e.Application.Cv.ExtractedText + "#").Length - 1,
            MissingMustHave = e.Skills.Any(s => !s.IsMatched && s.JobSkill.SkillType == SkillTypes.MustHave)
        }).ToListAsync(ct);
        return rows.ToDictionary(e => e.EvaluationId, e => new Snapshot(e.TextLength,
            ReviewFlags.Evaluate(new(e.Status, e.KeywordScore, e.AiScore, e.FinalScore, e.ScoreSource,
                e.TextLength, e.KeywordWeight, e.AiWeight, e.MissingMustHave, e.Reasoning), options)
            .Select(f => new ReviewFlagDto { Code = f.Code, Message = f.Message }).ToList()));
    }
}
