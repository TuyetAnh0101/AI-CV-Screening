using System.Globalization;
using SmartCv.Core.Constants;
using SmartCv.Core.Export;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

public enum RankingExportScope { Shortlisted, All, Current }

/// <summary>Immutable intent captured from the actual table, including its local filters and search privacy.</summary>
public sealed record RankingExportContext(int RunId, int JobId, string JobTitle, string RunStatus,
    string? Stage, RankingFilter Fit, string Search, bool NeedsReview, bool ManualFlags, bool ViewAnonymous, RankingSort Sort)
{
    public (RankingExportQuery? Query, string? Error) Build(RankingExportScope scope, bool anonymous)
    {
        if (RunId <= 0 || JobId <= 0) return (null, "Chưa xác định được lượt chấm và vị trí. Hãy tải lại kết quả.");
        if (RunStatus != RunStatuses.Completed) return (null, "Chỉ xuất khi lượt chấm đã hoàn tất. Hãy đợi rồi tải lại kết quả.");
        if (!Enum.IsDefined(scope)) return (null, "Phạm vi xuất không hợp lệ. Hãy chọn lại.");
        var current = scope == RankingExportScope.Current;
        var search = current && !string.IsNullOrWhiteSpace(Search) ? Search.Trim() : null;
        if (search is not null && ViewAnonymous != anonymous)
            return (null, "Để giữ đúng tìm kiếm hiện tại, chế độ ẩn danh của file phải khớp với bảng. Hãy đổi lựa chọn Ẩn danh hoặc đóng hộp thoại và xóa từ khóa.");
        var fit = current ? Fit switch { RankingFilter.All => "all", RankingFilter.Pass => "pass", RankingFilter.NotPass => "notpass", RankingFilter.Unscored => "unscored", _ => null } : "all";
        if (fit is null) return (null, "API chưa hỗ trợ bộ lọc dự đoán này. Hãy chọn phạm vi khác.");
        var stage = current ? Stage ?? "all" : scope == RankingExportScope.Shortlisted ? ApplicationStages.Shortlisted : "all";
        var query = new RankingExportQuery(JobId, "xlsx", stage, fit, search, anonymous, current && NeedsReview, current && ManualFlags);
        var (_, errors) = RankingExportQuery.Parse(Fields(query).ToDictionary(p => p.Key, p => new[] { p.Value }));
        return errors.Count == 0 ? (query, null) : (null, string.Join(" ", errors.Values.SelectMany(v => v)));
    }

    private static Dictionary<string, string> Fields(RankingExportQuery query)
    {
        var fields = new Dictionary<string, string>
        {
            ["jobId"] = query.JobId!.Value.ToString(CultureInfo.InvariantCulture), ["format"] = query.Format,
            ["stage"] = query.Stage, ["fit"] = query.Fit, ["anonymous"] = query.Anonymous ? "true" : "false",
            ["needsReview"] = query.NeedsReview ? "true" : "false", ["manualFlags"] = query.ManualFlags ? "true" : "false"
        };
        if (query.Search is not null) fields["search"] = query.Search;
        return fields;
    }
    public static string QueryString(RankingExportQuery query) => "?" + string.Join('&', Fields(query).Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));

    public static string Error<T>(ApiResult<T> result) => result.ErrorCode switch
    {
        "export_empty" => "Không có hồ sơ trong phạm vi đã chọn. Hãy đổi phạm vi hoặc bộ lọc.",
        "export_run_not_completed" => "Lượt chấm chưa hoàn tất hoặc dữ liệu vừa thay đổi. Hãy đóng hộp thoại và tải lại kết quả.",
        "export_too_many_rows" => "Số hồ sơ vượt giới hạn của một file. Hãy thu hẹp bộ lọc.",
        "export_pdf_disabled" => "PDF chưa được hỗ trợ. Hãy chọn Excel.",
        "password_change_required" => "Bạn cần đổi mật khẩu trước khi xuất kết quả.",
        _ => result.StatusCode switch
        {
            401 => "Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.",
            403 => "Bạn không có quyền xuất kết quả này. Phiên đăng nhập vẫn được giữ nguyên.",
            404 => "Không tìm thấy lượt chấm hoặc vị trí. Hãy tải lại kết quả.",
            409 => "Dữ liệu chưa đủ điều kiện xuất hoặc vừa thay đổi. Hãy kiểm tra lại bản xem trước.",
            503 => "Dịch vụ xuất tạm không khả dụng. Chưa nhận được file; vui lòng thử lại sau.",
            400 => "Phạm vi hoặc bộ lọc xuất không hợp lệ. Hãy kiểm tra lại lựa chọn.",
            _ => "Chưa tải được dữ liệu xuất. Vui lòng kiểm tra kết nối và thử lại."
        }
    };
}
