namespace SmartCv.Core.Contracts;

/// <summary>A19: application cohort uses CreatedAt; dates are inclusive Vietnam calendar dates.</summary>
public sealed class DashboardSummaryDto
{
    public int? JobId { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public int TotalCvs { get; init; }
    public int FailedCvs { get; init; }
    public int TotalApplications { get; init; }
    public int ActiveJobs { get; init; }
    public int RunningRuns { get; init; }
    public int FlaggedUnresolved { get; init; }
    public int? FlaggedJobId { get; init; }
    public List<FunnelStepDto> Funnel { get; init; } = [];
    public int RejectedCount { get; init; }
    public int OnHoldCount { get; init; }
    public ScreeningTimeDto ScreeningTime { get; init; } = new();
    public List<ReasonCountDto> RejectReasons { get; init; } = [];
    public List<RunSummaryDto> RecentRuns { get; init; } = [];
    public bool IsEmpty => TotalCvs == 0 && TotalApplications == 0;
}

public sealed class FunnelStepDto
{
    public string Stage { get; init; } = string.Empty;
    public string StageName { get; init; } = string.Empty;
    public int Current { get; init; }
    public int Reached { get; init; }
    public decimal? ConversionPercent { get; init; }
}

public sealed class ScreeningTimeDto
{
    public int Count { get; init; }
    public decimal? AverageHours { get; init; }
    public decimal? MedianHours { get; init; }
}

public sealed class ReasonCountDto
{
    public string Reason { get; init; } = string.Empty;
    public string ReasonName { get; init; } = string.Empty;
    public bool IsProfessional { get; init; }
    public int Count { get; init; }
    public decimal Percent { get; init; }
}
