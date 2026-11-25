using System.Globalization;
using Microsoft.AspNetCore.Http;
using SmartCv.Core.Contracts;

namespace SmartCv.Api.Reports;

/// <summary>Bounded reporting interval. Date boundaries use UTC+07:00 and an exclusive UTC end.</summary>
public sealed record AdminReportFilter(DateOnly From, DateOnly To, DateTime FromUtc,
    DateTime UntilUtc, int Days, int Page, int PageSize)
{
    public const int MaxDays = 90;
    public const int MaxDownloadEvents = 1000;
    public AdminReportPeriodDto ToDto(DateTime capturedAtUtc) =>
        new(From, To, "Asia/Ho_Chi_Minh (UTC+07:00)", capturedAtUtc);

    public static (AdminReportFilter? Filter, string? Error) Parse(
        IQueryCollection query, DateTimeOffset now, bool paging = false, bool csv = false)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "from", "to" };
        if (paging) { allowed.Add("page"); allowed.Add("pageSize"); }
        if (csv) allowed.Add("format");
        if (query.Keys.Any(key => !allowed.Contains(key))) return (null, "Có tham số không hỗ trợ.");

        string? Single(string key, out bool valid)
        {
            valid = true;
            if (!query.TryGetValue(key, out var items)) return null;
            if (items.Count != 1 || string.IsNullOrWhiteSpace(items[0]) || items[0]!.Length > 30)
            { valid = false; return null; }
            return items[0];
        }
        var fromText = Single("from", out var fromValid);
        var toText = Single("to", out var toValid);
        if (!fromValid || !toValid || (fromText is null) != (toText is null))
            return (null, "Cần gửi cả from và to, mỗi tham số đúng một giá trị yyyy-MM-dd.");

        var today = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(7)).DateTime);
        var to = today;
        var from = today.AddDays(-13);
        if (fromText is not null)
        {
            if (!DateOnly.TryParseExact(fromText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out from) ||
                !DateOnly.TryParseExact(toText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out to))
                return (null, "Ngày không đúng định dạng yyyy-MM-dd.");
        }
        if (from > to || to > today || (to.DayNumber - from.DayNumber + 1) > MaxDays)
            return (null, "Khoảng ngày phải từ 1 đến 90 ngày và không vượt quá hôm nay (giờ Việt Nam).");

        var page = 1;
        var size = 20;
        if (paging)
        {
            var rawPage = Single("page", out var pageValid);
            var rawSize = Single("pageSize", out var sizeValid);
            if (!pageValid || !sizeValid ||
                (rawPage is not null && (!int.TryParse(rawPage, NumberStyles.None,
                    CultureInfo.InvariantCulture, out page) || page is < 1 or > 100_000)) ||
                (rawSize is not null && (!int.TryParse(rawSize, NumberStyles.None,
                    CultureInfo.InvariantCulture, out size) || size is < 1 or > 100)))
                return (null, "page phải từ 1 đến 100000; pageSize từ 1 đến 100.");
        }
        if (csv)
        {
            var format = Single("format", out var valid);
            if (!valid || format is not (null or "csv"))
                return (null, "Chỉ hỗ trợ format=csv.");
        }
        // These DateTimeOffset constructors reject illegal offsets/time boundaries.
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(7));
        var until = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(7));
        return (new(from, to, start.UtcDateTime, until.UtcDateTime,
            to.DayNumber - from.DayNumber + 1, page, size), null);
    }
}
