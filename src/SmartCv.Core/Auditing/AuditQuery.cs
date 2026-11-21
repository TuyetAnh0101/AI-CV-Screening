using System.Globalization;
using SmartCv.Core.Common;

namespace SmartCv.Core.Auditing;

/// <summary>Bounded queries over Vietnam calendar dates; all persistence uses UTC.</summary>
public sealed record AuditQuery(DateTime FromUtc, DateTime UntilUtc, string? Search, string? Category,
    string? Action, int? ActorUserId, string? Outcome, string? ResourceType, string? ResourceId,
    string? RequestId, int Page, int PageSize)
{
    public int? WorkspaceId { get; init; }
    public static readonly IReadOnlyList<string> Categories = Array.AsReadOnly(new[] { "Authentication", "Accounts", "Cvs", "Candidates", "Recruitment", "Ai", "EvaluationSets", "Privacy" });
    public static readonly IReadOnlyList<string> Outcomes = Array.AsReadOnly(new[] { "Succeeded", "Denied", "Failed", "Partial", "NoChange", "AccessGranted" });

    public static (AuditQuery? Query, Dictionary<string, string[]> Errors) Parse(IReadOnlyDictionary<string, string[]> input, DateTime utcNow)
    {
        var errors = new Dictionary<string, string[]>();
        foreach (var pair in input.Where(p => p.Value.Length != 1)) errors[pair.Key] = ["Chỉ gửi trường này một lần."];
        string? Value(string key, int max = 80)
        {
            if (!input.TryGetValue(key, out var values)) return null;
            if (values.Length != 1) { errors[key] = ["Chỉ gửi trường này một lần."]; return null; }
            var value = values[0].Trim();
            if (value.Length > max || value.Any(char.IsControl)) { errors[key] = [$"Tối đa {max} ký tự, không có ký tự điều khiển."]; return null; }
            return value.Length == 0 ? null : value;
        }
        DateOnly Date(string key, DateOnly fallback)
        {
            var value = Value(key);
            if (value is null) return fallback;
            if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                && date.Year is >= 2000 and <= 9998) return date;
            errors[key] = ["Dùng ngày yyyy-MM-dd từ năm 2000 đến 9998."]; return fallback;
        }
        int Number(string key, int fallback, int max)
        {
            var value = Value(key);
            if (value is null) return fallback;
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 && number <= max) return number;
            errors[key] = [$"Giá trị phải từ 1 đến {max}."]; return fallback;
        }
        var today = VietnamTime.Today(utcNow);
        var to = Date("to", today); var from = Date("from", to.AddDays(-6));
        if (to < from || to.DayNumber - from.DayNumber >= 90) errors["from"] = ["Chọn khoảng ngày theo thứ tự, tối đa 90 ngày."];
        var category = Value("category"); var outcome = Value("outcome");
        if (category is not null && !Categories.Contains(category)) errors["category"] = ["Loại hoạt động không hợp lệ."];
        if (outcome is not null && !Outcomes.Contains(outcome)) errors["outcome"] = ["Kết quả không hợp lệ."];
        var actor = input.ContainsKey("actorUserId") && !string.IsNullOrWhiteSpace(input["actorUserId"].FirstOrDefault()) ? Number("actorUserId", 0, int.MaxValue) : (int?)null;
        var page = Number("page", 1, 1_000_000); var pageSize = Number("pageSize", 25, 100);
        var search = Value("search", 100); var action = Value("action"); var type = Value("resourceType", 40);
        var resource = Value("resourceId"); var request = Value("requestId", 64);
        int? workspace = input.ContainsKey("workspaceId") ? Number("workspaceId", 0, int.MaxValue) : null;
        if (workspace == 0) errors["workspaceId"] = ["Mã không gian làm việc phải là số nguyên dương."];
        return errors.Count > 0 ? (null, errors) : (new(VietnamTime.StartOfDayUtc(from), VietnamTime.StartOfDayUtc(to.AddDays(1)),
            search, category, action, actor, outcome, type, resource, request, page, pageSize) { WorkspaceId = workspace }, errors);
    }
}
