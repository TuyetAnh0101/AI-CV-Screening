namespace SmartCv.Core.Ops;

public sealed record HealthCheckResult(string Name, bool Ok, string Message);
public sealed record HealthReportDto(string Status, DateTime Time, IReadOnlyList<HealthCheckResult> Checks);

public static class HealthRules
{
    public static HealthReportDto Combine(IReadOnlyList<HealthCheckResult> checks, DateTime utcNow) =>
        new(checks.Count > 0 && checks.All(c => c.Ok) ? "ok" : "error", utcNow, checks);

    public static string[] OcrFiles(string languages)
    {
        var parts = languages.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length is 0 or > 10 || parts.Any(p => p.Length is 0 or > 40 || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')))
            throw new ArgumentException("Cấu hình ngôn ngữ OCR không hợp lệ.");
        return parts.Distinct(StringComparer.Ordinal).Select(p => p + ".traineddata").ToArray();
    }
}
