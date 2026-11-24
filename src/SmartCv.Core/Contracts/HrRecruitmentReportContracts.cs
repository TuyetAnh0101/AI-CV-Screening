namespace SmartCv.Core.Contracts;

/// <summary>Every report is scoped to the currently validated workspace on the API.</summary>
public sealed record HrReportStageCountDto(string Stage, string Label, int Count);
public sealed record HrReportRejectReasonDto(string Code, string Label, int Count);
public sealed record HrReportFitCountDto(string Fit, int Count);

public sealed record HrReportSummaryDto
{
    public int Applications { get; init; }
    public int UniqueCvs { get; init; }
    public int KnownUniqueCandidates { get; init; }
    public int ApplicationsWithoutCandidate { get; init; }
    public int EvaluatedApplications { get; init; }
    public int AiCombined { get; init; }
    public int KeywordOnly { get; init; }
    public int NoCompletedEvaluation { get; init; }
    public decimal? AverageFinalScore { get; init; }
    public int PassedScore { get; init; }
    public int BelowThreshold { get; init; }
    public int EverPassedCvRound { get; init; }
    public decimal HrPassedPercent { get; init; }
    public decimal CurrentRejectedPercent { get; init; }
    public int NeedsAction { get; init; }
    public decimal? AverageHoursToFirstDecision { get; init; }
    public int FirstDecisionSampleCount { get; init; }
    public IReadOnlyList<HrReportStageCountDto> Stages { get; init; } = [];
    public IReadOnlyList<HrReportRejectReasonDto> RejectReasons { get; init; } = [];
    public IReadOnlyList<HrReportFitCountDto> Fits { get; init; } = [];
    public int MaxExportRows { get; init; }
    public string ScoreRule { get; init; } = "Latest Screening evaluation per application; PromptTest excluded";
    public string PeriodRule { get; init; } = "Application.CreatedAt, Vietnam calendar dates (UTC+7)";
    public string StageRule { get; init; } = "Current application stage; historical passed counts application events";
}

/// <summary>One line = one Application, not one CV, Candidate or AI request.</summary>
public sealed record HrReportRowDto
{
    public int ApplicationId { get; init; }
    public string Applicant { get; init; } = "";
    public int JobId { get; init; }
    public string JobTitle { get; init; } = "";
    public DateTime AppliedAtUtc { get; init; }
    public string Stage { get; init; } = "";
    public string StageText { get; init; } = "";
    public string? RejectReasonText { get; init; }
    public int? RunId { get; init; }
    public decimal? FinalScore { get; init; }
    public decimal? PassThreshold { get; init; }
    public string? ScoreSource { get; init; }
    public string? EvaluationStatus { get; init; }
    public string? FitLabel { get; init; }
    public bool? PassedScore { get; init; }
    public bool EverPassedCvRound { get; init; }
    public DateTime? FirstDecisionUtc { get; init; }
    public bool NeedsAction { get; init; }
}

public sealed record HrReportPageDto(int Page, int PageSize, int TotalRows, int TotalPages,
    bool Anonymous, IReadOnlyList<HrReportRowDto> Items);

public sealed record HrReportPreviewDto(int TotalRows, int MaxExportRows, bool Anonymous,
    bool CanExport, string? BlockCode, string? BlockReason, string Scope);
