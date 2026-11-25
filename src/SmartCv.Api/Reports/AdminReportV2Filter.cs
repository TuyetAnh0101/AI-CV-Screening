using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using SmartCv.Core.Contracts;

namespace SmartCv.Api.Reports;

/// <summary>
/// Strict v2 selector; preserves v1 AdminReportFilter.Parse unchanged. Date boundaries remain
/// the same 1–90 inclusive local days using the already-approved UTC+7 filter.
/// </summary>
public sealed record AdminReportV2Filter(AdminReportFilter Period, string Report, string Format)
{
    public bool IncludeExportJournal => AdminReportV2Kinds.IncludesExports(Report);
    public bool IncludeHealth => Report is AdminReportV2Kinds.All or AdminReportV2Kinds.Operations;

    public static (AdminReportV2Filter? Filter, string? Error) Parse(IQueryCollection query, DateTimeOffset now)
    {
        if (query.Keys.Any(key => key is not ("from" or "to" or "report" or "format")))
            return (null, "Báo cáo chỉ hỗ trợ from, to, report và format.");
        static string? One(IQueryCollection q, string key, out bool valid)
        {
            valid = true;
            if (!q.TryGetValue(key, out var values)) return null;
            if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]) || values[0]!.Length > 30)
            { valid = false; return null; }
            return values[0];
        }
        var report = One(query, "report", out var goodReport) ?? AdminReportV2Kinds.All;
        var format = One(query, "format", out var goodFormat) ?? "xlsx";
        if (!goodReport || !goodFormat || !AdminReportV2Kinds.Allowed.Contains(report, StringComparer.Ordinal)
            || format is not ("xlsx" or "csv"))
            return (null, "Chỉ hỗ trợ report=all/accounts/workspaces/ai/operations/security/exports và format=xlsx/csv.");

        // Let the existing audited v1 period validation determine inclusive VN calendar dates.
        var onlyDate = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        if (query.TryGetValue("from", out var from)) onlyDate.Add("from", from);
        if (query.TryGetValue("to", out var to)) onlyDate.Add("to", to);
        var (period, error) = AdminReportFilter.Parse(new QueryCollection(onlyDate), now);
        return period is null ? (null, error) : (new(period, report, format), null);
    }

    public static IReadOnlyList<string> IncludedSections(string report) => report switch
    {
        AdminReportV2Kinds.All => ["overview", "accounts", "workspaces", "ai", "operations", "security", "exports", "sources"],
        AdminReportV2Kinds.Accounts => ["overview", "accounts", "sources"],
        AdminReportV2Kinds.Workspaces => ["overview", "workspaces", "sources"],
        AdminReportV2Kinds.Ai => ["overview", "ai", "sources"],
        AdminReportV2Kinds.Operations => ["overview", "operations", "sources"],
        AdminReportV2Kinds.Security => ["overview", "security", "sources"],
        AdminReportV2Kinds.Exports => ["overview", "exports", "sources"],
        _ => throw new ArgumentOutOfRangeException(nameof(report))
    };
}
