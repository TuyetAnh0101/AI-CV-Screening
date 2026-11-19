using System.Globalization;
using SmartCv.Core.Constants;

namespace SmartCv.Web.Helpers;

/// <summary>SCApp v2: bộ lọc bước "Chọn CV đã quét" (gửi lên API, tìm kiếm chữ chạy tại trình duyệt).</summary>
public sealed record CandidatePickerFilter(int? JobId = null, string? Stage = null, DateOnly? From = null, DateOnly? To = null, bool NotInSet = true)
{
    public string Query
    {
        get
        {
            var parts = new List<string>();
            if (JobId is { } id) parts.Add("jobId=" + id.ToString(CultureInfo.InvariantCulture));
            if (ApplicationStages.IsValid(Stage)) parts.Add("stage=" + Stage);
            if (From is { } from) parts.Add("from=" + from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (To is { } to) parts.Add("to=" + to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (NotInSet) parts.Add("notInSet=true");
            return parts.Count == 0 ? "" : "?" + string.Join('&', parts);
        }
    }
}
