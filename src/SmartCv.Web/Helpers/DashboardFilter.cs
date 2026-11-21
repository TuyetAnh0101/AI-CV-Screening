using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using SmartCv.Core.Common;
using SmartCv.Core.Dashboard;

namespace SmartCv.Web.Helpers;

/// <summary>URL is the applied filter. Invalid input never falls back to an unfiltered summary.</summary>
public sealed record DashboardFilter(int? JobId = null, DateOnly? From = null, DateOnly? To = null)
{
    public bool HasAny => JobId is not null || From is not null || To is not null;

    public string Query
    {
        get
        {
            var parts = new List<string>();
            if (JobId is { } id) parts.Add("jobId=" + id.ToString(CultureInfo.InvariantCulture));
            if (From is { } from) parts.Add("from=" + from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (To is { } to) parts.Add("to=" + to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            return parts.Count == 0 ? "" : "?" + string.Join('&', parts);
        }
    }

    //public string Url => "/" + Query;
    public string Url => "/dashboard" + Query;

    public static (DashboardFilter? Filter, string? Error) Parse(string? job, string? from, string? to)
    {
        int? jobId = null;
        if (!string.IsNullOrEmpty(job))
        {
            if (!int.TryParse(job, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                return (null, "Vị trí phải có mã là số nguyên dương hợp lệ. Hãy chọn lại vị trí.");
            jobId = id;
        }
        var (period, error) = DashboardPeriod.Parse(from, to);
        return period is null ? (null, error) : (new(jobId, period.From, period.To), null);
    }

    public static (DashboardFilter? Filter, string? Error) FromUri(string uri)
    {
        var query = QueryHelpers.ParseQuery(new Uri(uri).Query);
        foreach (var key in new[] { "jobId", "from", "to" })
            if (query.TryGetValue(key, out var values) && values.Count > 1)
                return (null, "Mỗi bộ lọc chỉ được có một giá trị. Hãy sửa địa chỉ hoặc xóa bộ lọc.");
        return Parse(query.GetValueOrDefault("jobId").ToString(), query.GetValueOrDefault("from").ToString(), query.GetValueOrDefault("to").ToString());
    }

    public DashboardFilter Quick(int? days, DateTime? utcNow = null)
    {
        if (days is null) return this with { From = null, To = null };
        if (days is not (7 or 30 or 90)) throw new ArgumentOutOfRangeException(nameof(days));
        var today = VietnamTime.Today(utcNow);
        return this with { From = today.AddDays(1 - days.Value), To = today };
    }
}
