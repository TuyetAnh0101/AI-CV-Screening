using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using SmartCv.Core.Candidates;
using SmartCv.Core.Cvs;

namespace SmartCv.Web.Helpers;

/// <summary>
/// B24: bộ lọc danh sách NGƯỜI (GET /api/candidates). Theo đúng contract A24, không ép quy tắc tìm kiếm của kho CV (B23):
/// từ khóa 1–254 ký tự sau khi gộp khoảng trắng; page từ 1; pageSize 1–100.
/// Từ khóa có thể là tên / email / số điện thoại nên KHÔNG ghi lên địa chỉ trang (chỉ giữ trong phiên, xem CandidateHubState).
/// </summary>
public sealed record CandidateListFilter(string Search = "", int Page = 1, int PageSize = CandidateListFilter.DefaultPageSize)
{
    public const int DefaultPageSize = 20;
    public const int MaxSearchLength = CandidateRules.MaxEmailLength;

    public string? NormalizedSearch => CvListQuery.NormalizeSearch(Search);
    public bool HasSearch => NormalizedSearch is not null;

    public (CandidateListFilter? Filter, string? Error) Validate()
    {
        if (NormalizedSearch is { Length: > MaxSearchLength }) return (null, $"Từ khóa tối đa {MaxSearchLength} ký tự.");
        if (Page < 1) return (null, "Số trang không hợp lệ.");
        if (PageSize is < 1 or > CvListQuery.MaxPageSize) return (null, "Số dòng mỗi trang không hợp lệ.");
        return (this with { Search = NormalizedSearch ?? "" }, null);
    }

    public string ApiQuery
    {
        get
        {
            var q = $"page={Page.ToString(CultureInfo.InvariantCulture)}&pageSize={PageSize.ToString(CultureInfo.InvariantCulture)}";
            return NormalizedSearch is { } s ? q + "&search=" + Uri.EscapeDataString(s) : q;
        }
    }

    /// <summary>Địa chỉ chế độ Ứng viên: chỉ số trang (không có từ khóa).</summary>
    public string Url => Page > 1 ? $"/candidates?page={Page.ToString(CultureInfo.InvariantCulture)}" : "/candidates";

    /// <summary>Đọc số trang từ địa chỉ; tham số lặp / sai → lỗi, không bao giờ tự rơi về trang khác.</summary>
    public static (int? Page, string? Error) PageFromUri(string uri)
    {
        var query = QueryHelpers.ParseQuery(new Uri(uri).Query);
        if (!query.TryGetValue("page", out var values)) return (1, null);
        if (values.Count > 1) return (null, "Đường dẫn có số trang lặp lại. Hãy về trang đầu.");
        return int.TryParse(values.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page >= 1
            ? (page, null) : (null, "Số trang trên đường dẫn không hợp lệ. Hãy về trang đầu.");
    }
}

/// <summary>B24: hai chế độ của trang Ứng viên dùng chung một địa chỉ gốc.</summary>
public static class CandidatesView
{
    public const string People = "people";
    public const string Cvs = "cvs";

    /// <summary>?view=cvs → Hồ sơ CV; còn lại (thiếu, rỗng, giá trị lạ) → Ứng viên.</summary>
    public static string FromUri(string uri)
    {
        var query = QueryHelpers.ParseQuery(new Uri(uri).Query);
        return string.Equals(query.GetValueOrDefault("view").ToString(), Cvs, StringComparison.OrdinalIgnoreCase) ? Cvs : People;
    }

    /// <summary>/cvs?x=1 (B23) → /candidates?view=cvs&amp;x=1: giữ nguyên bộ lọc cũ để bookmark vẫn dùng được.</summary>
    public static string LegacyCvsTarget(string uri)
    {
        var query = new Uri(uri).Query.TrimStart('?');
        var kept = string.Join('&', query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("view=", StringComparison.OrdinalIgnoreCase)));
        return "/candidates?view=cvs" + (kept.Length > 0 ? "&" + kept : "");
    }
}
