using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using SmartCv.Core.Constants;
using SmartCv.Core.Runs;

namespace SmartCv.Web.Helpers;

/// <summary>
/// B26: the /runs URL is the applied filter (type, status, from, to, page). Invalid input never becomes "all runs"
/// and never reaches the API. Dates are inclusive Vietnam calendar days (UTC+7), as A25 expects.
/// </summary>
public sealed record RunHistoryFilter(string? Type = null, string? Status = null, DateOnly? From = null, DateOnly? To = null, int Page = 1)
{
    /// <summary>Rows per page on /runs (A25 accepts 1–100).</summary>
    public const int PageSize = 20;

    public static readonly IReadOnlyList<(string Value, string Label)> Types =
        [(RunTypes.Screening, "Sàng lọc (Screening)"), (RunTypes.PromptTest, "Đánh giá prompt (PromptTest)")];

    public static readonly IReadOnlyList<(string Value, string Label)> Statuses =
        [(RunStatuses.Queued, "Đang chờ"), (RunStatuses.Running, "Đang phân tích"), (RunStatuses.Completed, "Hoàn thành"), (RunStatuses.Failed, "Thất bại")];

    private static readonly string[] Keys = ["type", "status", "from", "to", "page"];

    public bool HasCriteria => Type is not null || Status is not null || From is not null || To is not null;

    /// <summary>Canonical query, page omitted when 1: "?type=PromptTest&amp;status=Completed&amp;page=2".</summary>
    public string Query
    {
        get
        {
            var parts = new List<string>();
            if (Type is not null) parts.Add("type=" + Type);
            if (Status is not null) parts.Add("status=" + Status);
            if (From is { } from) parts.Add("from=" + Day(from));
            if (To is { } to) parts.Add("to=" + Day(to));
            if (Page > 1) parts.Add("page=" + Page.ToString(CultureInfo.InvariantCulture));
            return parts.Count == 0 ? "" : "?" + string.Join('&', parts);
        }
    }

    public string Url => "/runs" + Query;

    /// <summary>GET /api/runs query for A25's paged contract (always sends page, so the API returns RunPageDto).</summary>
    public string ApiQuery
    {
        get
        {
            var parts = new List<string> { "page=" + Page.ToString(CultureInfo.InvariantCulture), "pageSize=" + PageSize.ToString(CultureInfo.InvariantCulture) };
            if (Type is not null) parts.Add("type=" + Type);
            if (Status is not null) parts.Add("status=" + Status);
            if (From is { } from) parts.Add("from=" + Day(from));
            if (To is { } to) parts.Add("to=" + Day(to));
            return string.Join('&', parts);
        }
    }

    public RunHistoryFilter WithPage(int page) => this with { Page = page };

    public static (RunHistoryFilter? Filter, string? Error) Parse(string? type, string? status, string? from, string? to, string? page)
    {
        string? typeValue = null, statusValue = null;
        if (!string.IsNullOrEmpty(type))
        {
            typeValue = Types.Select(t => t.Value).FirstOrDefault(v => string.Equals(v, type, StringComparison.OrdinalIgnoreCase));
            if (typeValue is null) return (null, "Loại lượt không hợp lệ. Hãy chọn Sàng lọc hoặc Đánh giá prompt.");
        }
        if (!string.IsNullOrEmpty(status))
        {
            statusValue = Statuses.Select(s => s.Value).FirstOrDefault(v => string.Equals(v, status, StringComparison.OrdinalIgnoreCase));
            if (statusValue is null) return (null, "Trạng thái không hợp lệ. Hãy chọn lại trạng thái lượt chấm.");
        }
        if (!TryDay(from, out var fromDay) || !TryDay(to, out var toDay))
            return (null, "Ngày phải tồn tại và có định dạng yyyy-MM-dd.");
        if (fromDay > toDay) return (null, "Từ ngày phải trước hoặc bằng Đến ngày.");
        var pageValue = 1;
        if (!string.IsNullOrEmpty(page)
            && (!int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out pageValue) || pageValue < 1))
            return (null, "Số trang phải là số nguyên từ 1 trở lên.");

        // Final guard: exactly the A25 server rules (UTC-representable days, page × pageSize overflow).
        var (_, serverError) = RunHistoryQuery.Create(typeValue, statusValue, fromDay, toDay, null, pageValue, PageSize);
        if (serverError is not null)
            return (null, pageValue > 1 && fromDay is null && toDay is null
                ? "Số trang vượt giới hạn phân trang. Hãy về trang 1."
                : "Khoảng ngày hoặc số trang nằm ngoài giới hạn tra cứu.");
        return (new(typeValue, statusValue, fromDay, toDay, pageValue), null);
    }

    public static (RunHistoryFilter? Filter, string? Error) FromUri(string uri)
    {
        var query = QueryHelpers.ParseQuery(new Uri(uri).Query);
        foreach (var key in Keys)
            if (query.TryGetValue(key, out var values) && values.Count > 1)
                return (null, "Mỗi bộ lọc chỉ được có một giá trị. Hãy sửa địa chỉ hoặc bỏ lọc.");
        string? Value(string key) => query.TryGetValue(key, out var v) ? v.ToString() : null;
        return Parse(Value("type"), Value("status"), Value("from"), Value("to"), Value("page"));
    }

    private static bool TryDay(string? text, out DateOnly? day)
    {
        day = null;
        if (string.IsNullOrEmpty(text)) return true;
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)) return false;
        day = value;
        return true;
    }

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
