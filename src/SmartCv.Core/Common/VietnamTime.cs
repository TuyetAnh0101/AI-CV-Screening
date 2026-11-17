namespace SmartCv.Core.Common;

/// <summary>Vietnam calendar days use a fixed UTC+7 offset, independent of the host timezone.</summary>
public static class VietnamTime
{
    public static DateOnly Today(DateTime? utcNow = null) =>
        DateOnly.FromDateTime((utcNow ?? DateTime.UtcNow).AddHours(7));

    public static DateTime StartOfDayUtc(DateOnly day) =>
        DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue).AddHours(-7), DateTimeKind.Utc);

    /// <summary>Inclusive calendar dates become a half-open UTC interval. Unrepresentable bounds throw.</summary>
    public static (DateTime? FromUtc, DateTime? ToUtc) RangeUtc(DateOnly? from, DateOnly? to) =>
        (from is { } f ? StartOfDayUtc(f) : null, to is { } t ? StartOfDayUtc(t.AddDays(1)) : null);
}
