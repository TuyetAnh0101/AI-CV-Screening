using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;

namespace SmartCv.Api.Reports;

/// <summary>Internal, bounded report projection. No CV text/prompt is ever loaded.</summary>
public sealed record HrApplicationFact(
    int ApplicationId, Guid CvId, int? CandidateId, string? CandidateName,
    int JobId, string JobTitle, DateTime CreatedAtUtc, string Stage,
    string? RejectReason, DateTime? FirstDecisionUtc, bool EverPassed,
    bool HasOpenManualFlag);

public sealed record HrScoreFact(
    int ApplicationId, int RunId, string Status, string? Source,
    decimal? Score, decimal Threshold, string? FitLabel);

public sealed record HrReportFact(HrApplicationFact Application, HrScoreFact? Score)
{
    public bool Scored => Score is { Status: EvaluationStatuses.Completed, Score: not null };
    public bool Pass => Scored && Score!.Score!.Value >= Score.Threshold;
    public bool NeedsAction => Application.Stage is ApplicationStages.New or ApplicationStages.Reviewing
                               || Application.HasOpenManualFlag;
}

public static class HrReportRollup
{
    public static bool Include(HrReportFact row, string evaluation) => evaluation switch
    {
        "all" => true,
        "Combined" => row.Scored && row.Score!.Source == ScoreSources.Combined,
        "KeywordOnly" => row.Scored && row.Score!.Source == ScoreSources.KeywordOnly,
        "Failed" => row.Score?.Status == EvaluationStatuses.Failed,
        "Unscored" => !row.Scored && row.Score?.Status != EvaluationStatuses.Failed,
        _ => false
    };

    public static HrReportSummaryDto Summary(IReadOnlyList<HrReportFact> rows)
    {
        var count = rows.Count;
        var scored = rows.Where(x => x.Scored).ToArray();
        var passed = rows.Count(x => x.Application.EverPassed);
        var rejected = rows.Count(x => x.Application.Stage == ApplicationStages.Rejected);
        var withDecision = rows.Where(x => x.Application.FirstDecisionUtc.HasValue).ToArray();
        var stages = ApplicationStages.All.Select(stage => new HrReportStageCountDto(
            stage, ApplicationStages.DisplayName(stage), rows.Count(r => r.Application.Stage == stage))).ToArray();
        var reasons = rows.Where(x => x.Application.Stage == ApplicationStages.Rejected &&
                                      RejectReasons.IsValid(x.Application.RejectReason))
            .GroupBy(x => x.Application.RejectReason!)
            .Select(g => new HrReportRejectReasonDto(g.Key, RejectReasons.DisplayName(g.Key), g.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Code, StringComparer.Ordinal).ToArray();
        var fits = scored.Where(x => !string.IsNullOrWhiteSpace(x.Score!.FitLabel))
            .GroupBy(x => x.Score!.FitLabel!).Select(g => new HrReportFitCountDto(g.Key, g.Count()))
            .OrderByDescending(x => x.Count).ToArray();
        return new HrReportSummaryDto
        {
            Applications = count,
            UniqueCvs = rows.Select(x => x.Application.CvId).Distinct().Count(),
            KnownUniqueCandidates = rows.Where(x => x.Application.CandidateId.HasValue)
                .Select(x => x.Application.CandidateId!.Value).Distinct().Count(),
            ApplicationsWithoutCandidate = rows.Count(x => !x.Application.CandidateId.HasValue),
            EvaluatedApplications = scored.Length,
            AiCombined = scored.Count(x => x.Score!.Source == ScoreSources.Combined),
            KeywordOnly = scored.Count(x => x.Score!.Source == ScoreSources.KeywordOnly),
            NoCompletedEvaluation = count - scored.Length,
            AverageFinalScore = scored.Length == 0 ? null : Math.Round(scored.Average(x => x.Score!.Score!.Value), 2),
            PassedScore = scored.Count(x => x.Pass),
            BelowThreshold = scored.Count(x => !x.Pass),
            EverPassedCvRound = passed,
            HrPassedPercent = Percent(passed, count),
            CurrentRejectedPercent = Percent(rejected, count),
            NeedsAction = rows.Count(x => x.NeedsAction),
            FirstDecisionSampleCount = withDecision.Length,
            AverageHoursToFirstDecision = withDecision.Length == 0 ? null : Math.Round((decimal)withDecision
                .Average(x => Math.Max(0, (x.Application.FirstDecisionUtc!.Value - x.Application.CreatedAtUtc).TotalHours)), 2),
            Stages = stages, RejectReasons = reasons, Fits = fits,
            MaxExportRows = HrReportFilter.MaxExportRows
        };
    }

    public static decimal Percent(int numerator, int denominator) => denominator == 0 ? 0 :
        Math.Round(100m * numerator / denominator, 2);

    public static HrReportRowDto Row(HrReportFact input, bool anonymous)
    {
        var a = input.Application; var s = input.Score;
        return new HrReportRowDto
        {
            ApplicationId = a.ApplicationId,
            Applicant = anonymous || string.IsNullOrWhiteSpace(a.CandidateName)
                ? $"HS-{a.ApplicationId:000000}" : a.CandidateName,
            JobId = a.JobId,
            JobTitle = a.JobTitle,
            AppliedAtUtc = a.CreatedAtUtc,
            Stage = a.Stage,
            StageText = ApplicationStages.DisplayName(a.Stage),
            RejectReasonText = RejectReasons.IsValid(a.RejectReason) ? RejectReasons.DisplayName(a.RejectReason) : null,
            RunId = s?.RunId, FinalScore = input.Scored ? s?.Score : null,
            PassThreshold = input.Scored ? s?.Threshold : null,
            ScoreSource = input.Scored ? s?.Source : null,
            EvaluationStatus = s?.Status, FitLabel = input.Scored ? s?.FitLabel : null,
            PassedScore = input.Scored ? input.Pass : null,
            EverPassedCvRound = a.EverPassed,
            FirstDecisionUtc = a.FirstDecisionUtc,
            NeedsAction = input.NeedsAction
        };
    }
}
