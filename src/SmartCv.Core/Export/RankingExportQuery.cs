using System.Globalization;
using System.Text;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Scoring;

namespace SmartCv.Core.Export;

/// <summary>Export uses the current Results filters and preserves the ranking's original ranks/order.</summary>
public sealed record RankingExportQuery(int? JobId, string Format, string Stage, string Fit,
    string? Search, bool Anonymous, bool NeedsReview, bool ManualFlags)
{
    public static IReadOnlyList<string> Fits { get; } = Array.AsReadOnly(new[]
        { "all", "pass", "notpass", "unscored", "excellent", "good", "potential", "poor", "issue" });
    public static (RankingExportQuery? Query, Dictionary<string, string[]> Errors) Parse(IReadOnlyDictionary<string, string[]> input)
    {
        var errors = new Dictionary<string, string[]>();
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "jobId", "format", "stage", "fit", "search", "anonymous", "needsReview", "manualFlags" };
        foreach (var key in input.Keys.Where(k => !allowed.Contains(k))) errors[key] = ["Tham số không được hỗ trợ."];
        string? Value(string key)
        {
            if (!input.TryGetValue(key, out var values)) return null;
            if (values.Length != 1) { errors[key] = ["Chỉ gửi tham số này một lần."]; return null; }
            var value = values[0].Trim();
            if (value.Length == 0 || value.Any(char.IsControl)) { errors[key] = ["Không để trống hoặc dùng ký tự điều khiển."]; return null; }
            return value;
        }
        bool Boolean(string key, bool fallback)
        {
            var value = Value(key);
            if (value is null) return fallback;
            if (bool.TryParse(value, out var result)) return result;
            errors[key] = ["Chỉ nhận true hoặc false."]; return fallback;
        }
        int? jobId = null;
        if (Value("jobId") is { } job)
        {
            if (int.TryParse(job, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0) jobId = id;
            else errors["jobId"] = ["Mã vị trí tuyển dụng phải là số nguyên dương."];
        }
        var format = Value("format")?.ToLowerInvariant() ?? "xlsx";
        if (format is not ("xlsx" or "pdf")) errors["format"] = ["Định dạng chỉ nhận xlsx hoặc pdf."];
        var stage = Value("stage") ?? ApplicationStages.Shortlisted;
        if (stage is not ("all" or "flagged") && !ApplicationStages.IsValid(stage)) errors["stage"] = ["Giai đoạn hồ sơ không hợp lệ."];
        var fit = Value("fit")?.ToLowerInvariant() ?? "all";
        if (!Fits.Contains(fit)) errors["fit"] = ["Bộ lọc mức phù hợp không hợp lệ."];
        var search = Value("search");
        if (search?.Length > 100) errors["search"] = ["Từ khóa tìm kiếm tối đa 100 ký tự."];
        var anonymous = Boolean("anonymous", true);
        var needsReview = Boolean("needsReview", false) || stage == "flagged";
        var manualFlags = Boolean("manualFlags", false);
        return errors.Count > 0 ? (null, errors) : (new(jobId, format, stage == "flagged" ? "all" : stage,
            fit, search, anonymous, needsReview, manualFlags), errors);
    }

    public List<RankingItemDto> Apply(IEnumerable<RankingItemDto> items) => items.Where(r =>
        (Stage == "all" || r.Stage == Stage) && (!NeedsReview || r.NeedsReview) && (!ManualFlags || r.ManualFlag is not null)
        && MatchFit(r) && MatchSearch(r)).ToList();

    private bool MatchFit(RankingItemDto r)
    {
        var scored = r.Status == EvaluationStatuses.Completed && r.FinalScore is not null;
        return Fit switch
        {
            "pass" => scored && r.PredictedPass == true,
            "notpass" => scored && r.PredictedPass != true,
            "unscored" or "issue" => !scored,
            "excellent" => scored && r.FitLabel == FitLevels.Excellent,
            "good" => scored && r.FitLabel == FitLevels.Good,
            "potential" => scored && r.FitLabel == FitLevels.Potential,
            "poor" => scored && r.FitLabel == FitLevels.Poor,
            _ => true
        };
    }
    private bool MatchSearch(RankingItemDto row)
    {
        var text = Anonymous ? Alias(row.Rank, row.ApplicationId) : row.CandidateName + " " + row.FileName;
        var folded = Fold(text);
        return Fold(Search).Split(' ', StringSplitOptions.RemoveEmptyEntries).All(folded.Contains);
    }
    // Same stable alias as AnonymityState; tied ranks remain distinguishable.
    public static string Alias(int? rank, int applicationId) => $"Ứng viên #{rank ?? applicationId} · HS{applicationId}";
    private static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var result = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                result.Append(ch is 'đ' or 'Đ' ? 'd' : char.ToLowerInvariant(ch));
        return string.Join(' ', result.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public string ScopeText() => string.Join(", ", new[]
    {
        Stage == "all" ? "Tất cả hồ sơ" : ApplicationStages.DisplayName(Stage),
        Fit == "all" ? null : $"Bộ lọc: {Fit}", NeedsReview ? "Cần xem kỹ" : null,
        ManualFlags ? "Có cờ HR" : null, Search is null ? null : "Có tìm kiếm",
        Anonymous ? "Ẩn danh" : "Có tên ứng viên"
    }.Where(s => s is not null));
}
