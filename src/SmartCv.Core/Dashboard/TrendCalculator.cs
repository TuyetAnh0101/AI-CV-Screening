using SmartCv.Core.Common;
using SmartCv.Core.Contracts;

namespace SmartCv.Core.Dashboard;

/// <summary>
/// SCApp v2: xu hướng hồ sơ ứng tuyển theo ngày/tuần Việt Nam (UTC+7).
/// Đầu vào là từng hồ sơ (CreatedAt UTC + đã qua vòng CV hay chưa) đã lọc theo Job và khoảng ngày.
/// </summary>
public static class TrendCalculator
{
    public const string Day = "day";
    public const string Week = "week";
    public const int MaxPoints = 120;
    /// <summary>Khi không chọn ngày bắt đầu: mặc định 30 ngày gần nhất.</summary>
    public const int DefaultDays = 30;

    public sealed record Row(DateTime CreatedAtUtc, bool PassedCvRound);

    /// <summary>"auto": tối đa 31 ngày theo ngày, dài hơn theo tuần.</summary>
    public static string ResolveBucket(string? requested, DateOnly from, DateOnly to)
    {
        if (requested is Day or Week) return requested;
        return to.DayNumber - from.DayNumber + 1 <= 31 ? Day : Week;
    }

    public static bool IsValidBucket(string? bucket) => string.IsNullOrEmpty(bucket) || bucket is Day or Week or "auto";

    /// <summary>Tuần bắt đầu Thứ Hai (lịch Việt Nam).</summary>
    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    public static int PointCount(string bucket, DateOnly from, DateOnly to) => bucket == Day
        ? to.DayNumber - from.DayNumber + 1
        : (WeekStart(to).DayNumber - WeekStart(from).DayNumber) / 7 + 1;

    public static DashboardTrendDto Build(int? jobId, DateOnly from, DateOnly to, string bucket, IEnumerable<Row> rows)
    {
        if (from > to) throw new ArgumentException("from > to", nameof(from));
        var points = new List<(DateOnly Start, DateOnly End)>();
        if (bucket == Day)
            for (var d = from; d <= to; d = d.AddDays(1)) points.Add((d, d));
        else
            for (var w = WeekStart(from); w <= to; w = w.AddDays(7))
                points.Add((w < from ? from : w, w.AddDays(6) > to ? to : w.AddDays(6)));

        var apps = new int[points.Count];
        var passed = new int[points.Count];
        foreach (var row in rows)
        {
            var day = DateOnly.FromDateTime(DateTime.SpecifyKind(row.CreatedAtUtc, DateTimeKind.Utc).AddHours(7));
            if (day < from || day > to) continue;   // ngoài khoảng: không tính
            var index = bucket == Day ? day.DayNumber - from.DayNumber
                : (WeekStart(day).DayNumber - WeekStart(from).DayNumber) / 7;
            apps[index]++;
            if (row.PassedCvRound) passed[index]++;
        }
        var total = apps.Sum();
        var totalPassed = passed.Sum();
        return new DashboardTrendDto
        {
            JobId = jobId, From = from, To = to, Bucket = bucket,
            TotalApplications = total, TotalPassed = totalPassed, PassRatePercent = Rate(totalPassed, total),
            Points = points.Select((p, i) => new TrendPointDto
            {
                Start = p.Start, End = p.End, Applications = apps[i], PassedCvRound = passed[i], PassRatePercent = Rate(passed[i], apps[i])
            }).ToList()
        };
    }

    /// <summary>Khoảng mặc định khi thiếu ngày: kết thúc hôm nay (UTC+7), bắt đầu 30 ngày trước ngày kết thúc.</summary>
    public static (DateOnly From, DateOnly To) DefaultRange(DateOnly? from, DateOnly? to, DateTime? utcNow = null)
    {
        var end = to ?? VietnamTime.Today(utcNow);
        var start = from ?? end.AddDays(1 - DefaultDays);
        return (start, end);
    }

    private static decimal? Rate(int part, int total) => total == 0 ? null
        : Math.Round(part * 100m / total, 1, MidpointRounding.AwayFromZero);
}
