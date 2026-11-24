using System.Globalization;
using SmartCv.Core.Constants;
using SmartCv.Core.Dashboard;

namespace SmartCv.Api.Reports;

/// <summary>Validated query. Dates filter Application.CreatedAt, not evaluation or decision date.</summary>
public sealed record HrReportFilter(
    int? JobId, int? RunId, DashboardPeriod Period, string Stage, string Evaluation,
    int Page, int PageSize, bool Anonymous, string Sort = "appliedAt", string Direction = "desc")
{
    public const int MaxApplicationsToAggregate = 20_000;
    public const int MaxExportRows = 2_000;
    public static readonly string[] EvaluationFilters = ["all", "Combined", "KeywordOnly", "Failed", "Unscored"];
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
        { "jobId", "runId", "from", "to", "stage", "evaluation", "page", "pageSize", "anonymous", "sort", "direction" };

    public static (HrReportFilter? Filter, string? Error) Parse(IQueryCollection query)
    {
        if (query.Keys.Any(k => !Allowed.Contains(k)) || query.Any(kv => kv.Value.Count != 1))
            return (null, "Tên bộ lọc không hợp lệ hoặc tham số bị lặp.");
        if (!Positive(query["jobId"].ToString(), out var jobId) || !Positive(query["runId"].ToString(), out var runId))
            return (null, "JobId và RunId phải là số nguyên dương.");
        var (period, error) = DashboardPeriod.Parse(query["from"].ToString(), query["to"].ToString());
        if (period is null) return (null, error);
        var stage = query["stage"].ToString();
        if (string.IsNullOrEmpty(stage)) stage = "all";
        if (stage != "all" && !ApplicationStages.IsValid(stage)) return (null, "Giai đoạn HR không hợp lệ.");
        var evaluation = query["evaluation"].ToString();
        if (string.IsNullOrEmpty(evaluation)) evaluation = "all";
        if (!EvaluationFilters.Contains(evaluation, StringComparer.Ordinal)) return (null, "Bộ lọc nguồn kết quả không hợp lệ.");
        if (!Bounded(query["page"].ToString(), 1, 100_000, 1, out var page) ||
            !Bounded(query["pageSize"].ToString(), 1, 100, 25, out var size))
            return (null, "Trang phải từ 1 đến 100000; kích thước trang từ 1 đến 100.");
        var anonym = query["anonymous"].ToString();
        if (anonym.Length != 0 && anonym is not ("true" or "false")) return (null, "anonymous chỉ nhận true hoặc false.");
        var sort = query["sort"].ToString();
        if (sort.Length == 0) sort = "appliedAt";
        if (!HrReportSort.Allowed.Contains(sort, StringComparer.Ordinal))
            return (null, "Trường sắp xếp không hợp lệ.");
        var direction = query["direction"].ToString();
        if (direction.Length == 0) direction = "desc";
        if (direction is not ("asc" or "desc"))
            return (null, "direction chỉ nhận asc hoặc desc.");
        return (new HrReportFilter(jobId, runId, period, stage, evaluation, page, size, anonym != "false", sort, direction), null);
    }

    private static bool Positive(string input, out int? value)
    {
        value = null;
        if (input.Length == 0) return true;
        if (!int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var i) || i <= 0) return false;
        value = i; return true;
    }

    private static bool Bounded(string input, int min, int max, int defaultValue, out int value)
    {
        value = defaultValue;
        return input.Length == 0 || (int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max);
    }

    public string Scope => $"Job: {(JobId?.ToString(CultureInfo.InvariantCulture) ?? "all")}; " +
        $"Run: {(RunId?.ToString(CultureInfo.InvariantCulture) ?? "latest Screening")}; " +
        $"From: {(Period.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "all")}; " +
        $"To: {(Period.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "all")}; " +
        $"Stage: {Stage}; Evaluation: {Evaluation}; Anonymous: {Anonymous}";
}
