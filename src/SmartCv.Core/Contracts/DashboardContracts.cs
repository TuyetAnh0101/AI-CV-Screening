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
    /// <summary>"Cần xem kỹ": hồ sơ Mới / Đang xem có cảnh báo tự động (lượt sàng lọc mới nhất) HOẶC cờ thủ công đang mở.</summary>
    public int FlaggedUnresolved { get; init; }
    public int ManualFlagsOpen { get; init; }                      // SCApp v2: trong đó, số hồ sơ có cờ thủ công đang mở
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

/// <summary>
/// SCApp v2: GET /api/dashboard/trend?jobId=&amp;from=&amp;to=&amp;bucket=day|week
/// Cột: số hồ sơ ứng tuyển mới (Applications.CreatedAt) theo ngày/tuần Việt Nam (UTC+7).
/// Đường: trong chính nhóm hồ sơ đó, tỷ lệ đã qua vòng CV (đang hoặc đã từng ở Qua vòng CV / Phỏng vấn).
/// Mỗi hồ sơ (ApplicationId) đếm một lần; một CV nộp hai Job là hai hồ sơ.
/// </summary>
public sealed class DashboardTrendDto
{
    public int? JobId { get; init; }
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public string Bucket { get; init; } = "day";                   // day | week (tuần bắt đầu Thứ Hai)
    public int TotalApplications { get; init; }
    public int TotalPassed { get; init; }
    public decimal? PassRatePercent { get; init; }
    public List<TrendPointDto> Points { get; init; } = [];
}

public sealed class TrendPointDto
{
    public DateOnly Start { get; init; }
    public DateOnly End { get; init; }                             // bao gồm
    public int Applications { get; init; }
    public int PassedCvRound { get; init; }
    /// <summary>null khi nhóm không có hồ sơ (không vẽ điểm của đường).</summary>
    public decimal? PassRatePercent { get; init; }
}
