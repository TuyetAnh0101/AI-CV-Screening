using System.Globalization;
using SmartCv.Core.Contracts;

namespace SmartCv.Web.Helpers;

public static class DashboardText
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    public static string Number(long value) => value.ToString("N0", Vi);
    public static string Percent(decimal? value) => value is null ? "—" : value.Value.ToString("0.#", Vi) + "%";
    public static string Hours(decimal? hours) => hours switch
    {
        null or < 0 => "—",
        0 => "0 phút",
        < 1 => $"{Math.Max(1, Math.Round(hours.Value * 60, 0, MidpointRounding.AwayFromZero)).ToString("0", Vi)} phút",
        < 48 => hours.Value.ToString("0.#", Vi) + " giờ",
        _ => (hours.Value / 24).ToString("0.#", Vi) + " ngày"
    };

    /// <summary>Only the visual width is calculated; displayed counts and percentages stay API values.</summary>
    public static string Width(int count, int maximum) => maximum <= 0 || count <= 0 ? "0"
        : Math.Clamp(100m * count / maximum, 0, 100).ToString("0.###", CultureInfo.InvariantCulture);

    public static string FlaggedLink(DashboardSummaryDto summary)
    {
        var job = summary.JobId is > 0 ? summary.JobId : summary.FlaggedJobId is > 0 ? summary.FlaggedJobId : null;
        return job is null ? "/results?filter=flagged" : $"/results?jobId={job.Value.ToString(CultureInfo.InvariantCulture)}&filter=flagged";
    }

    public static string RunType(string? type) => type switch
    { "Screening" => "Sàng lọc", "PromptTest" => "Đánh giá Prompt", _ => "Lượt chấm" };

    public static string Period(DashboardFilter filter) => (filter.From, filter.To) switch
    {
        (null, null) => "Tất cả thời gian",
        ({ } from, null) => $"Từ {from:dd/MM/yyyy}",
        (null, { } to) => $"Đến {to:dd/MM/yyyy}",
        ({ } from, { } to) => $"{from:dd/MM/yyyy} – {to:dd/MM/yyyy}"
    };
}
