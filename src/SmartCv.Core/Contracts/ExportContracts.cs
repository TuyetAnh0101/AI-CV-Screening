using SmartCv.Core.Constants;
using SmartCv.Core.Export;

namespace SmartCv.Core.Contracts;

public sealed class ExportSettingsDto
{
    public string[] Formats { get; init; } = ["xlsx"];
    public bool PdfEnabled { get; init; }
    public int MaxRows { get; init; }
    public string DefaultStage { get; init; } = ApplicationStages.Shortlisted;
    public bool DefaultAnonymous { get; init; } = true;
    public string Sort { get; init; } = "finalDesc";
    public IReadOnlyList<string> Stages { get; init; } = ["all", .. ApplicationStages.All];
    public IReadOnlyList<string> Fits { get; init; } = RankingExportQuery.Fits;
    public bool CanExport { get; init; } = true;
    public bool CanViewLogs { get; init; }
}

public sealed record RankingExportPreviewDto(int RunId, int JobId, string RunStatus, int TotalCount, int RowCount,
    int ShortlistedCount, int MaxRows, bool Anonymous, string Scope, bool CanExport, string? BlockCode, string? BlockReason);

public sealed record ExportLogDto(long ExportId, long AuditEventId, int RunId, int JobId, string JobTitle,
    string Format, string Stage, string Fit, bool SearchApplied, bool NeedsReview, bool ManualFlags,
    int RowCount, bool Anonymized, int? UserId, string ExportedBy, DateTime CreatedAt);

public sealed record ExportLogPageDto(int Page, int PageSize, int TotalCount, int TotalPages, List<ExportLogDto> Items);
