namespace SmartCv.Core.Scoring;

/// <summary>Một lượt chấm đã xong: số hồ sơ và thời gian chạy (CompletedAt − CreatedAt, gồm cả thời gian chờ trong hàng đợi).</summary>
public sealed record RunDurationSample(int TotalItems, TimeSpan Duration);

/// <summary>Kết quả ước tính. IsDefault: chưa có lượt cũ hợp lệ, dùng giá trị mặc định.</summary>
public sealed record RunTimeEstimate(
    int Count,
    decimal SecondsPerCv,
    int EstimatedSeconds,
    int EstimatedMinutes,
    int BasedOnRuns,
    decimal MinSecondsPerCv,
    string Text)
{
    public bool IsDefault => BasedOnRuns == 0;
}

/// <summary>
/// A42: ước tính thời gian chấm N CV từ các lượt Screening đã xong. Hàm thuần, unit test được.
///   giây/CV = tổng thời gian các lượt / tổng số hồ sơ của chúng (lượt nhiều CV nặng hơn, không lấy trung bình theo lượt).
///   Không thấp hơn khoảng cách tối thiểu giữa hai lần gọi AI (sàn): chấm nhanh hơn thế là không thể.
///   Chưa có lượt hợp lệ → sàn + 2 giây (ví dụ 4,5 + 2 = 6,5 giây/CV).
/// Bỏ mẫu không dùng được: không có hồ sơ, thời gian ≤ 0, hoặc trên 1 giờ/CV (lượt bị treo / worker tắt nhiều ngày).
/// Chỉ là ƯỚC TÍNH: phụ thuộc hàng đợi, số lần thử lại khi Gemini quá tải (503), không phải cam kết thời gian.
/// </summary>
public static class RunTimeEstimator
{
    public const int MaxSamples = 10;
    public const int MaxCount = 500;                       // cùng giới hạn số hồ sơ một lượt chấm (POST /api/runs)
    public const decimal DefaultOverheadSeconds = 2m;      // thời gian AI trả lời + chấm keyword + ghi database cho một CV
    public const decimal MaxSecondsPerCvSample = 3600m;    // trên 1 giờ/CV: lượt bất thường, không dùng

    /// <param name="minDelayBetweenCallsMs">Khoảng cách tối thiểu giữa hai lần gọi AI của chế độ đang chạy (Gemini thật hoặc AI giả).</param>
    public static RunTimeEstimate Estimate(int count, IEnumerable<RunDurationSample> samples, int minDelayBetweenCallsMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxCount);
        ArgumentNullException.ThrowIfNull(samples);

        var floor = Math.Max(0, minDelayBetweenCallsMs) / 1000m;
        var valid = samples
            .Where(s => s is not null && s.TotalItems > 0 && s.Duration > TimeSpan.Zero
                        && (decimal)s.Duration.TotalSeconds / s.TotalItems <= MaxSecondsPerCvSample)
            .Take(MaxSamples)
            .ToList();

        var perCv = valid.Count == 0
            ? floor + DefaultOverheadSeconds
            : valid.Sum(s => (decimal)s.Duration.TotalSeconds) / valid.Sum(s => (long)s.TotalItems);
        perCv = Math.Round(Math.Max(perCv, floor), 1, MidpointRounding.AwayFromZero);

        // count ≤ 500 và perCv ≤ 3600 (hoặc sàn cấu hình ≤ 600 giây) nên không tràn int
        var seconds = (int)Math.Ceiling(perCv * count);
        var minutes = (int)Math.Ceiling(seconds / 60m);
        return new RunTimeEstimate(count, perCv, seconds, minutes, valid.Count, floor, Describe(count, seconds));
    }

    /// <summary>"0 CV", "3 CV · dưới 1 phút", "10 CV · khoảng 2 phút", "200 CV · khoảng 1 giờ 15 phút".</summary>
    public static string Describe(int count, int seconds)
    {
        if (count == 0) return "0 CV";
        if (seconds < 60) return $"{count} CV · dưới 1 phút";
        var minutes = (int)Math.Ceiling(seconds / 60m);
        var time = minutes < 60 ? $"{minutes} phút"
            : minutes % 60 == 0 ? $"{minutes / 60} giờ" : $"{minutes / 60} giờ {minutes % 60} phút";
        return $"{count} CV · khoảng {time}";
    }
}
