namespace SmartCv.Core.Workspaces;

public sealed record WorkspaceDto(
    int Id,
    string Name,
    string Kind,
    string Role,
    int? DailyAiLimit,
    int AiUsedToday,
    DateTimeOffset AiResetsAt,
    int? MaxCvCount,
    int CvCount,
    bool HasActivePrompt,
    int JobCount);