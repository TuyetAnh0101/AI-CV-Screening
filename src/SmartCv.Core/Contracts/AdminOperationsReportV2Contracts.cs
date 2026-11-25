namespace SmartCv.Core.Contracts;

/// <summary>Stable v2 export contract. No CV, candidate, prompt, token or personal user fields.</summary>
public static class AdminReportV2Kinds
{
    public const string All = "all";
    public const string Accounts = "accounts";
    public const string Workspaces = "workspaces";
    public const string Ai = "ai";
    public const string Operations = "operations";
    public const string Security = "security";
    public const string Exports = "exports";
    public static readonly IReadOnlyList<string> Allowed =
        [All, Accounts, Workspaces, Ai, Operations, Security, Exports];

    public static bool IncludesExports(string kind) => kind is All or Exports;
}

public sealed record AdminReportV2PreviewDto(
    AdminReportPeriodDto Period,
    string Report,
    string Format,
    IReadOnlyList<string> IncludedSections,
    long? ExportEventCount,
    int MaxExportEvents,
    bool CanDownload,
    string? BlockCode,
    IReadOnlyList<string> Notices);

/// <summary>Only comparable event-in-period metrics; never compare snapshot balances as if historical.</summary>
public sealed record AdminReportV2ComparisonRowDto(
    string Metric, long Current, long Previous, decimal? ChangePercent, string Source);
