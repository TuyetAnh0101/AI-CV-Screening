using System.Globalization;
using SmartCv.Core.Common;

namespace SmartCv.Core.Dashboard;

public sealed record DashboardPeriod(DateOnly? From, DateOnly? To, DateTime? FromUtc, DateTime? ToUtc)
{
    public static (DashboardPeriod? Period, string? Error) Parse(string? from, string? to)
    {
        if (!TryDate(from, out var first) || !TryDate(to, out var last))
            return (null, "Ngày phải tồn tại và có định dạng yyyy-MM-dd.");
        return Create(first, last);
    }

    public static (DashboardPeriod? Period, string? Error) Create(DateOnly? from, DateOnly? to)
    {
        if (from > to) return (null, "Ngày bắt đầu phải trước hoặc bằng ngày kết thúc.");
        try
        {
            var (start, end) = VietnamTime.RangeUtc(from, to);
            return (new(from, to, start, end), null);
        }
        catch (ArgumentOutOfRangeException)
        {
            return (null, "Ngày nằm ngoài khoảng thời gian UTC có thể biểu diễn.");
        }
    }

    public bool Contains(DateTime utc) => (FromUtc is null || utc >= FromUtc) && (ToUtc is null || utc < ToUtc);

    private static bool TryDate(string? value, out DateOnly? day)
    {
        day = null;
        if (string.IsNullOrEmpty(value)) return true;
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        day = parsed;
        return true;
    }
}
