using System.Globalization;
using SmartCv.Core.Common;
using SmartCv.Core.Constants;

namespace SmartCv.Core.Runs;

/// <summary>Validated filters use half-open UTC bounds for inclusive Vietnamese calendar days.</summary>
public sealed record RunHistoryQuery(string? Type, string? Status, DateTime? FromUtc, DateTime? ToUtc,
    int? JobId, int Page, int PageSize, bool IsPaged, int Take)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    private static readonly string[] Fields = ["type", "status", "from", "to", "jobId", "take", "page", "pageSize"];
    public int Skip => checked((Page - 1) * PageSize);

    public static (RunHistoryQuery? Query, Dictionary<string, string[]> Errors) Parse(IReadOnlyDictionary<string, string[]> parameters)
    {
        var errors = new Dictionary<string, string[]>();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            var entries = parameters.Where(p => p.Key.Equals(field, StringComparison.OrdinalIgnoreCase)).SelectMany(p => p.Value).ToArray();
            if (entries.Length > 1) errors[field] = ["Mỗi tham số chỉ được gửi một lần."];
            else if (entries.Length == 1)
            {
                if (string.IsNullOrWhiteSpace(entries[0])) errors[field] = ["Tham số không được để trống."];
                else values[field] = entries[0];
            }
        }
        int? Integer(string field)
        {
            if (!values.TryGetValue(field, out var text)) return null;
            if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result)) return result;
            errors[field] = ["Giá trị phải là số nguyên trong phạm vi Int32."]; return null;
        }
        DateOnly? Day(string field)
        {
            if (!values.TryGetValue(field, out var text)) return null;
            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)) return result;
            errors[field] = ["Ngày phải tồn tại và có định dạng yyyy-MM-dd."]; return null;
        }
        var jobId = Integer("jobId"); var take = Integer("take"); var page = Integer("page"); var size = Integer("pageSize");
        var from = Day("from"); var to = Day("to");
        if (errors.Count > 0) return (null, errors);
        return Validate(values.GetValueOrDefault("type"), values.GetValueOrDefault("status"), from, to, jobId, page, size, take ?? 20);
    }

    // Compatible pure entry point for the ZIP examples; Parse additionally reports per-field errors.
    public static (RunHistoryQuery? Query, string? Error) Create(string? type, string? status,
        DateOnly? from, DateOnly? to, int? jobId, int? page, int? pageSize)
    {
        var (query, errors) = Validate(type, status, from, to, jobId, page, pageSize, 20);
        return (query, errors.Count == 0 ? null : string.Join(" ", errors.Values.SelectMany(v => v)));
    }

    private static (RunHistoryQuery? Query, Dictionary<string, string[]> Errors) Validate(string? type, string? status,
        DateOnly? from, DateOnly? to, int? jobId, int? page, int? pageSize, int take)
    {
        var errors = new Dictionary<string, string[]>();
        if (type is not null and not (RunTypes.Screening or RunTypes.PromptTest)) errors["type"] = ["type phải là Screening hoặc PromptTest."];
        if (status is not null and not (RunStatuses.Queued or RunStatuses.Running or RunStatuses.Completed or RunStatuses.Failed))
            errors["status"] = ["status phải là Queued, Running, Completed hoặc Failed."];
        if (jobId is <= 0) errors["jobId"] = ["jobId phải là số nguyên dương."];
        if (from > to) errors["from"] = ["Ngày bắt đầu phải trước hoặc bằng ngày kết thúc."];
        if (page is < 1) errors["page"] = ["page bắt đầu từ 1."];
        if (pageSize is < 1 or > MaxPageSize) errors["pageSize"] = ["pageSize phải từ 1 đến 100."];
        if (pageSize is not null && page is null) errors["pageSize"] = ["Chỉ gửi pageSize khi có page."];
        var actualPage = page ?? 1; var actualSize = pageSize ?? DefaultPageSize;
        if ((actualPage - 1L) * actualSize > int.MaxValue) errors["page"] = ["Vị trí trang vượt giới hạn phân trang. Hãy giảm page hoặc pageSize."];
        DateTime? first = null, end = null;
        try { if (from is { } f) first = VietnamTime.StartOfDayUtc(f); }
        catch (ArgumentOutOfRangeException) { errors["from"] = ["Ngày bắt đầu nằm ngoài khoảng UTC có thể biểu diễn."]; }
        try { if (to is { } t) end = VietnamTime.StartOfDayUtc(t.AddDays(1)); }
        catch (ArgumentOutOfRangeException) { errors["to"] = ["Ngày kết thúc nằm ngoài khoảng UTC có thể biểu diễn."]; }
        return errors.Count > 0 ? (null, errors) : (new(type, status, first, end, jobId, actualPage, actualSize, page is not null, Math.Clamp(take, 1, 100)), errors);
    }

    public static int TotalPages(int totalCount, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalCount);
        if (pageSize is < 1 or > MaxPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));
        return (int)((totalCount + (long)pageSize - 1) / pageSize);
    }

    /// <summary>Legacy int contract: clamp corrupt/extreme durations rather than overflow.</summary>
    public static int? DurationSeconds(DateTime createdAt, DateTime? completedAt) => completedAt is { } end
        ? (int)Math.Clamp((end.Ticks - createdAt.Ticks) / TimeSpan.TicksPerSecond, 0L, int.MaxValue) : null;
}
