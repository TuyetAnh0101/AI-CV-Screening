using Microsoft.AspNetCore.WebUtilities;
using SmartCv.Core.Cvs;

namespace SmartCv.Web.Helpers;

/// <summary>B23 uses A23 validation verbatim; invalid filters never become an unfiltered request.</summary>
public sealed record CvLibraryFilter(string Search = "", int? JobId = null, string Stage = "", string Source = "",
    string Status = "", int Page = 1, int PageSize = CvListQuery.DefaultPageSize, string Candidate = "")
{
    /// <summary>B24: địa chỉ chuẩn của chế độ Hồ sơ CV trong trang Ứng viên (/cvs cũ chuyển hướng về đây).</summary>
    public const string BasePath = "/candidates?view=cvs";
    public bool HasFilter => Search.Length > 0 || JobId is not null || Stage.Length > 0 || Source.Length > 0 || Status.Length > 0 || Candidate.Length > 0;

    public static (CvLibraryFilter? Filter, string? Error) FromUri(string uri)
    {
        var raw = QueryHelpers.ParseQuery(new Uri(uri).Query);
        var (query, errors) = CvListQuery.Parse(raw.ToDictionary(p => p.Key, p => p.Value.Select(v => v ?? "").ToArray()));
        return query is null ? (null, ValidationMessage(errors)) : (FromQuery(query), null);
    }

    public (CvLibraryFilter? Filter, string? Error) Validate()
    {
        var (q, error) = CvListQuery.Create(Search, JobId, Stage, Source, Status, Page, PageSize, Candidate);
        return q is null ? (null, SafeValidation(error)) : (FromQuery(q), null);
    }

    private static CvLibraryFilter FromQuery(CvListQuery q) => new(q.Search ?? "", q.JobId, q.Stage ?? "", q.Source ?? "", q.Status ?? "", q.Page, q.PageSize, q.Candidate ?? "");
    private static string ValidationMessage(Dictionary<string, string[]> errors) => errors.ContainsKey("search")
        ? "Từ khóa cần từ 2 đến 100 ký tự sau khi gộp khoảng trắng."
        : "Đường dẫn có bộ lọc hoặc số trang không hợp lệ. Hãy chọn lại hoặc xóa bộ lọc.";
    private static string SafeValidation(string? error) => error?.StartsWith("Từ khóa", StringComparison.Ordinal) == true
        ? "Từ khóa cần từ 2 đến 100 ký tự sau khi gộp khoảng trắng."
        : "Bộ lọc hoặc số trang không hợp lệ. Hãy chọn lại hoặc xóa bộ lọc.";

    public string ApiQuery => string.Join("&", Parts(includeDefaults: true));
    public string Url => BasePath + (Parts(false).ToArray() is { Length: > 0 } parts ? "&" + string.Join("&", parts) : "");
    private IEnumerable<string> Parts(bool includeDefaults)
    {
        var search = CvListQuery.NormalizeSearch(Search);
        if (search is not null) yield return "search=" + Uri.EscapeDataString(search);
        if (JobId is { } id) yield return $"jobId={id}";
        if (Stage.Length > 0) yield return "stage=" + Uri.EscapeDataString(Stage);
        if (Source.Length > 0) yield return "source=" + Uri.EscapeDataString(Source);
        if (Status.Length > 0) yield return "status=" + Uri.EscapeDataString(Status);
        if (Candidate.Length > 0) yield return "candidate=" + Uri.EscapeDataString(Candidate);
        if (includeDefaults || Page != 1) yield return $"page={Page}";
        if (includeDefaults || PageSize != CvListQuery.DefaultPageSize) yield return $"pageSize={PageSize}";
    }
}
