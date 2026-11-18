using System.Globalization;
using SmartCv.Core.Constants;

namespace SmartCv.Core.Cvs;

/// <summary>
/// A23: bộ lọc + phân trang của GET /api/cvs (kho CV). Hàm thuần → unit test được.
///   search : tìm trong tên file, tên ứng viên, email ứng viên, chữ đọc từ CV (2–100 ký tự sau khi gộp khoảng trắng)
///   jobId  : CV đã ứng tuyển job này
///   stage  : có hồ sơ ở giai đoạn này (nếu có jobId: giai đoạn ở đúng job đó)
///   source : kênh nhận CV (Email | Website | JobBoard | Referral | Other)
///   status : Pending | Completed | Failed (trạng thái đọc chữ) | Anonymized (đã ẩn danh hóa)
///   page từ 1, pageSize 1–100 (mặc định 20)
/// Giá trị rỗng = không lọc. Mã stage/source/status phân biệt hoa thường (đúng mã lưu trong database).
/// Hồ sơ đã ẩn danh hóa không còn tên, email, chữ → khi có search thì bỏ qua chúng.
/// </summary>
public sealed record CvListQuery(string? Search, int? JobId, string? Stage, string? Source, string? Status, int Page, int PageSize)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const int MinSearchLength = 2;
    public const int MaxSearchLength = 100;
    public const string AnonymizedStatus = "Anonymized";

    public static readonly string[] Statuses =
        [ExtractionStatuses.Pending, ExtractionStatuses.Completed, ExtractionStatuses.Failed, AnonymizedStatus];

    private static readonly string[] Fields = ["search", "jobId", "stage", "source", "status", "page", "pageSize"];

    /// <summary>Số dòng bỏ qua; Create/Parse đã chặn page × pageSize vượt Int32.</summary>
    public int Skip => checked((Page - 1) * PageSize);

    /// <summary>
    /// Đọc thẳng query string (A25 cùng cách): tham số gửi lặp hoặc số nguyên sai → lỗi theo từng trường,
    /// thay vì để framework trả 400 không có thông báo. Tên tham số không phân biệt hoa thường; tham số lạ bị bỏ qua.
    /// </summary>
    public static (CvListQuery? Query, Dictionary<string, string[]> Errors) Parse(IReadOnlyDictionary<string, string[]> parameters)
    {
        var errors = new Dictionary<string, string[]>();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            var entries = parameters.Where(p => p.Key.Equals(field, StringComparison.OrdinalIgnoreCase)).SelectMany(p => p.Value).ToArray();
            if (entries.Length > 1) errors[field] = ["Mỗi tham số chỉ được gửi một lần."];
            else if (entries.Length == 1 && !string.IsNullOrWhiteSpace(entries[0])) values[field] = entries[0];
        }
        int? Integer(string field)
        {
            if (!values.TryGetValue(field, out var text)) return null;
            if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) return number;
            errors[field] = [$"{field} phải là số nguyên."];
            return null;
        }
        var jobId = Integer("jobId"); var page = Integer("page"); var pageSize = Integer("pageSize");
        if (errors.Count > 0) return (null, errors);
        return Validate(values.GetValueOrDefault("search"), jobId, values.GetValueOrDefault("stage"),
            values.GetValueOrDefault("source"), values.GetValueOrDefault("status"), page, pageSize);
    }

    /// <summary>Lối vào thuần theo mẫu hướng dẫn A23: trả câu lỗi đầu tiên.</summary>
    public static (CvListQuery? Query, string? Error) Create(
        string? search, int? jobId, string? stage, string? source, string? status, int? page, int? pageSize)
    {
        var (query, errors) = Validate(search, jobId, stage, source, status, page, pageSize);
        return (query, errors.Count == 0 ? null : errors.Values.First()[0]);
    }

    private static (CvListQuery? Query, Dictionary<string, string[]> Errors) Validate(
        string? search, int? jobId, string? stage, string? source, string? status, int? page, int? pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        var s = NormalizeSearch(search);
        if (s is { Length: < MinSearchLength }) errors["search"] = [$"Từ khóa tìm kiếm cần ít nhất {MinSearchLength} ký tự."];
        else if (s is { Length: > MaxSearchLength }) errors["search"] = [$"Từ khóa tìm kiếm tối đa {MaxSearchLength} ký tự."];
        if (jobId is < 1) errors["jobId"] = ["jobId không hợp lệ."];
        var stageValue = Blank(stage);
        if (stageValue is not null && !ApplicationStages.IsValid(stageValue))
            errors["stage"] = [$"stage phải là một trong: {string.Join(", ", ApplicationStages.All)}."];
        var sourceValue = Blank(source);
        if (sourceValue is not null && !CvSourceChannels.IsValid(sourceValue))
            errors["source"] = [$"source phải là một trong: {string.Join(", ", CvSourceChannels.All)}."];
        var statusValue = Blank(status);
        if (statusValue is not null && !Statuses.Contains(statusValue, StringComparer.Ordinal))
            errors["status"] = [$"status phải là một trong: {string.Join(", ", Statuses)}."];
        if (page is < 1) errors["page"] = ["page bắt đầu từ 1."];
        if (pageSize is < 1 or > MaxPageSize) errors["pageSize"] = [$"pageSize từ 1 đến {MaxPageSize}."];
        var actualPage = page ?? 1;
        var actualSize = pageSize ?? DefaultPageSize;
        if (!errors.ContainsKey("page") && !errors.ContainsKey("pageSize") && (actualPage - 1L) * actualSize > int.MaxValue)
            errors["page"] = ["page vượt giới hạn phân trang. Hãy giảm page hoặc pageSize."];

        return errors.Count > 0
            ? (null, errors)
            : (new CvListQuery(s, jobId, stageValue, sourceValue, statusValue, actualPage, actualSize), errors);
    }

    /// <summary>Bỏ khoảng trắng đầu/cuối, gộp nhiều khoảng trắng liền nhau thành một. Rỗng → null (không tìm).</summary>
    public static string? NormalizeSearch(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return null;
        return string.Join(' ', search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static int TotalPages(int totalCount, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        return (int)((totalCount + (long)pageSize - 1) / pageSize);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
