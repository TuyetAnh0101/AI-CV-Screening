using System.Globalization;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;

namespace SmartCv.Web.Helpers;

/// <summary>B9 presentation only; never replaces scores returned by the API.</summary>
public static class Display
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    public static string Score(decimal? value) => value?.ToString("0.##", Vi) ?? "—";
    public static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    public static string Weight(decimal value) => Score(value * 100m) + "%";
    public static string VnTime(DateTime value)
    {
        if (value == default) return "—";
        var utc = value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
        return new DateTimeOffset(utc).ToOffset(TimeSpan.FromHours(7)).ToString("dd/MM/yyyy HH:mm 'UTC+7'", Vi);
    }

    public static string BackUrl(string? run, string? job)
    {
        var query = new List<string>();
        if (run is not null) query.Add("runId=" + Uri.EscapeDataString(run));
        if (job is not null) query.Add("jobId=" + Uri.EscapeDataString(job));
        return "/results" + (query.Count == 0 ? "" : "?" + string.Join("&", query));
    }

    public static decimal? WeightedTotal(EvaluationDetailDto d) => d.Status != EvaluationStatuses.Completed ? null
        : d.ScoreSource == ScoreSources.KeywordOnly ? d.KeywordScore
        : d.ScoreSource == ScoreSources.Combined && d.KeywordWeight >= 0 && d.AiWeight >= 0 && d.KeywordWeight + d.AiWeight == 1
            ? d.KeywordScore * d.KeywordWeight + d.AiScore * d.AiWeight : null;

    // A9 appends this exact suffix after applying the cap. A lower final score alone is not evidence.
    // The DTO has no cap snapshot: only report the backend's explicit recorded explanation.
    public static bool HasRecordedCap(EvaluationDetailDto d)
    {
        var missing = (d.Skills ?? []).Where(s => s.SkillType == "MustHave" && !s.IsMatched).ToList();
        if (missing.Count == 0 || d.FinalScore is null || WeightedTotal(d) is not { } total
            || Math.Round(total, 2, MidpointRounding.AwayFromZero) <= d.FinalScore) return false;
        var prefix = $" (Điểm cuối giới hạn {d.FinalScore.Value.ToString("0", CultureInfo.InvariantCulture)} vì thiếu kỹ năng bắt buộc: ";
        var reason = d.Reasoning ?? "";
        var index = reason.LastIndexOf(prefix, StringComparison.Ordinal);
        if (index < 0 || !reason.EndsWith(".)", StringComparison.Ordinal)) return false;
        var names = reason[(index + prefix.Length)..^2].Split(", ");
        return names.Length == missing.Count && names.Order().SequenceEqual(missing.Select(s => s.DisplayName).Order());
    }

    /// <summary>ErrorMessage may contain provider URLs, credentials or paths; expose only known safe messages.</summary>
    public static string SafeError(string? message)
    {
        if (message is "Không gọi được AI" or "CV chưa trích xuất được nội dung.") return message;
        if (message == "CV chưa trích xuất được nội dung (ExtractionStatus = Failed).") return "Trích xuất nội dung CV thất bại.";
        var status = System.Text.RegularExpressions.Regex.Match(message ?? "", @"\bHTTP (400|401|403|404|429|500|502|503|504)\b");
        if (status.Success) return $"Gemini báo {status.Value}. AI chưa chấm thành công.";
        return "Không thể hoàn tất xử lý. Vui lòng liên hệ quản trị viên để kiểm tra lỗi đã ghi nhận.";
    }
}
