using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Ai;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;
using SmartCv.Core.Evaluation;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Data.Seed;

namespace SmartCv.Tests;

/// <summary>
/// Recombine frozen seed scores and final skill flags with the real combiner.
/// This is deterministic algorithm replay, not a new CV extraction or Gemini call.
/// Inputs come from seed_demo.sql rather than a second hand-maintained score vector.
/// </summary>
public class HistoricalDemoReplayTests
{
    private sealed record Row(int EvaluationId, int RunId, int ApplicationId,
        decimal Keyword, decimal Ai, decimal HistoricalFinal, bool HistoricalPass);
    private sealed record Skill(int EvaluationId, int JobId, string Code, bool Matched);
    private sealed record Definition(string Name, bool MustHave);
    private sealed record Fixture(string Sql, Row[] Rows, Skill[] Skills,
        Dictionary<int, bool> Labels, Dictionary<(int, string), Definition> Definitions);

    private static Fixture ReadFixture()
    {
        var sql = File.ReadAllText(TestSupport.RepoFile("database/seed_demo.sql"));
        var rows = Regex.Matches(sql,
            @"^\((\d+), ([12]), (\d+), ([\d.]+), ([\d.]+), ([\d.]+), N'Combined', N'[^']*', ([01]),",
            RegexOptions.Multiline).Select(m => new Row(
                Int(m, 1), Int(m, 2), Int(m, 3), Number(m, 4), Number(m, 5), Number(m, 6), Int(m, 7) == 1)).ToArray();
        var skills = Regex.Matches(sql, @"^\((\d+), (\d+), N'([^']+)', ([01]),", RegexOptions.Multiline)
            .Select(m => new Skill(Int(m, 1), Int(m, 2), m.Groups[3].Value, Int(m, 4) == 1)).ToArray();
        var labels = Regex.Matches(sql, @"^\(1, (\d+), ([01]), N'", RegexOptions.Multiline)
            .ToDictionary(m => Int(m, 1), m => Int(m, 2) == 1);
        var definitions = JsonSerializer.SerializeToElement(JobSeedData.JobSkills).EnumerateArray().ToDictionary(
            j => (j.GetProperty("JobId").GetInt32(), j.GetProperty("SkillCode").GetString()!),
            j => new Definition(j.GetProperty("DisplayName").GetString()!,
                j.GetProperty("SkillType").GetString() == SkillTypes.MustHave));
        Assert.Equal(20, rows.Length);
        Assert.Equal(192, skills.Length);
        Assert.Equal(10, labels.Count);
        return new(sql, rows, skills, labels, definitions);
    }

    private static int Int(Match match, int group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
    private static decimal Number(Match match, int group) => decimal.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    private static CombinedScore Replay(Fixture fixture, Row row, decimal keywordWeight = 0.30m, decimal? cap = 69m)
    {
        var skills = fixture.Skills.Where(s => s.EvaluationId == row.EvaluationId).Select(s =>
        {
            var definition = fixture.Definitions[(s.JobId, s.Code)];
            // Saved flags already represent the final keyword/AI union. Its historical origin is unknown.
            return new SkillMatch(s.Code, definition.Name, definition.MustHave, s.Matched, null);
        }).ToArray();
        Assert.NotEmpty(skills);
        return ScoreCombiner.Combine(new(row.Keyword, skills, null, null, []),
            new AiEvaluationResult { IsSuccess = true, Score = row.Ai, IsSimulated = true,
                Reasoning = "Replay from frozen demo inputs; no Gemini call." },
            keywordWeight, 1m - keywordWeight, 70m, cap);
    }

    private static List<LabeledPrediction> Predictions(Fixture fixture, int run, decimal keywordWeight, decimal? cap) =>
        fixture.Rows.Where(r => r.RunId == run).Select(row => new LabeledPrediction(
            row.ApplicationId, fixture.Labels[row.ApplicationId], EvaluationStatuses.Completed,
            Replay(fixture, row, keywordWeight, cap).PredictedPass, ScoreSources.Combined)).ToList();

    [Fact]
    public void Seed_source_uses_30_70_and_historical_uncapped_scores_remain_exactly_reproducible()
    {
        var fixture = ReadFixture();
        var runs = Regex.Matches(fixture.Sql,
            @"^\(\d+, N'(?:PromptTest|Screening)', \d+, (?:\d+|NULL), N'[^']+', [\d.]+, ([\d.]+), ([\d.]+),",
            RegexOptions.Multiline);
        Assert.Equal(4, runs.Count);
        Assert.All(runs, r => Assert.Equal((0.30m, 0.70m), (Number(r, 1), Number(r, 2))));
        foreach (var row in fixture.Rows)
        {
            var replay = Replay(fixture, row, cap: null);
            Assert.Equal(row.HistoricalFinal, replay.FinalScore);
            Assert.Equal(row.HistoricalPass, replay.PredictedPass);
        }
    }

    [Theory]
    [InlineData(0.30, false, 7, 8, 2, 1)]
    [InlineData(0.40, false, 7, 8, 2, 1)]
    [InlineData(0.30, true, 9, 9, 0, 0)]
    [InlineData(0.40, true, 9, 9, 0, 0)]
    public void Frozen_demo_outcomes_change_due_to_cap_not_the_30_70_or_40_60_weight_pair(
        double weight, bool capEnabled, int correctV1, int correctV2, int wrongV1, int wrongV2)
    {
        var fixture = ReadFixture();
        var expected = new[] { (correctV1, wrongV1), (correctV2, wrongV2) };
        foreach (var run in new[] { 1, 2 })
        {
            var result = PromptAccuracyCalculator.Calculate(Predictions(fixture, run, (decimal)weight, capEnabled ? 69m : null));
            Assert.Equal((10, expected[run - 1].Item1, 1, expected[run - 1].Item2, 0),
                (result.Total, result.Correct, result.Missed, result.WrongPick, result.Unscored));
            Assert.Equal(expected[run - 1].Item1 * 10m, result.AccuracyPercent);
        }
    }

    [Fact]
    public void Capped_frozen_replay_is_same_90_percent_and_different_weights_are_not_fair()
    {
        var fixture = ReadFixture();
        var settings = new RunSettings(1, "gemini-2.5-flash", 0.20m, 0.30m, 0.70m, 70m);
        var v1 = new CompareRun(101, "Frozen replay v1", RunStatuses.Completed, settings, Predictions(fixture, 1, 0.30m, 69m));
        var v2 = new CompareRun(102, "Frozen replay v2", RunStatuses.Completed, settings, Predictions(fixture, 2, 0.30m, 69m));
        var same = PromptComparer.Compare(v1, v2);
        Assert.Equal((90m, 90m, 0m), (same.Base.AccuracyPercent, same.Candidate.AccuracyPercent, same.AccuracyDelta));
        Assert.Equal(CompareVerdicts.Same, same.Verdict);
        Assert.True(same.IsFairComparison);
        Assert.Equal((0, 0, 10, 0), (same.FixedCount, same.BrokenCount, same.UnchangedCount, same.UnscoredCount));
        var different = PromptComparer.Compare(v1, v2 with { Settings = settings with { KeywordWeight = 0.40m, AiWeight = 0.60m } });
        Assert.False(different.IsFairComparison);
        Assert.Equal(CompareVerdicts.NotComparable, different.Verdict);
        Assert.Contains(different.Warnings, w => w.Contains("trọng số"));
        foreach (var row in fixture.Rows.Where(r => r.ApplicationId == 5 || r.RunId == 1 && r.ApplicationId == 9))
        {
            var replay = Replay(fixture, row);
            Assert.Equal(69m, replay.FinalScore);
            Assert.False(replay.PredictedPass);
            Assert.Contains("giới hạn 69", replay.Reasoning);
        }
    }

    [Fact]
    public async Task Adding_calculated_replay_runs_preserves_existing_historical_scores_predictions_and_accuracy()
    {
        var fixture = ReadFixture();
        await using var db = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>()
            .UseInMemoryDatabase("HistoricalReplay-" + Guid.NewGuid()).Options);
        await db.Database.EnsureCreatedAsync();
        var existing = await PromptCompareSeed.DemoAsync(db);
        var oldIds = new[] { existing.RunV1, existing.RunV2 };
        async Task<string> History() => JsonSerializer.Serialize(new
        {
            Runs = await db.EvaluationRuns.AsNoTracking().Where(r => oldIds.Contains(r.RunId)).OrderBy(r => r.RunId)
                .Select(r => new { r.RunId, r.KeywordWeight, r.AiWeight, r.AccuracyPercent, r.Status }).ToArrayAsync(),
            Scores = await db.Evaluations.AsNoTracking().Where(e => oldIds.Contains(e.RunId)).OrderBy(e => e.EvaluationId)
                .Select(e => new { e.EvaluationId, e.KeywordScore, e.AiScore, e.FinalScore, e.PredictedPass, e.Reasoning }).ToArrayAsync()
        });
        var before = await History();
        foreach (var version in new[] { 1, 2 })
        {
            var run = new EvaluationRun
            {
                RunType = RunTypes.PromptTest, PromptVersionId = version == 1 ? existing.PromptV1 : existing.PromptV2,
                EvaluationSetId = existing.Set, ModelName = "gemini-2.5-flash", Temperature = 0.20m,
                KeywordWeight = 0.30m, AiWeight = 0.70m, PassThreshold = 70m, Status = RunStatuses.Completed,
                TotalItems = 10, ProcessedItems = 10, CreatedAt = PromptCompareSeed.Day.AddDays(10),
                CompletedAt = PromptCompareSeed.Day.AddDays(10),
                AccuracyPercent = PromptAccuracyCalculator.Calculate(Predictions(fixture, version, 0.30m, 69m)).AccuracyPercent
            };
            foreach (var row in fixture.Rows.Where(r => r.RunId == version))
            {
                var score = Replay(fixture, row);
                run.Evaluations.Add(new SmartCv.Core.Entities.Evaluation
                {
                    ApplicationId = existing.Apps[row.ApplicationId - 1], KeywordScore = score.KeywordScore,
                    AiScore = score.AiScore, FinalScore = score.FinalScore, FitLabel = score.FitLabel,
                    PredictedPass = score.PredictedPass, ScoreSource = score.ScoreSource,
                    Status = EvaluationStatuses.Completed, Reasoning = score.Reasoning
                });
            }
            db.EvaluationRuns.Add(run);
            await db.SaveChangesAsync();
        }
        Assert.Equal(before, await History());
        Assert.Equal(4, await db.EvaluationRuns.CountAsync());
        Assert.All(await db.EvaluationRuns.Where(r => !oldIds.Contains(r.RunId)).ToListAsync(), r => Assert.Equal(90m, r.AccuracyPercent));
    }
}
