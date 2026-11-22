namespace SmartCv.Core.Options;

/// <summary>
/// Giới hạn mặc định khi tạo workspace cá nhân.
/// Workspace nội bộ không áp dụng các giới hạn này.
/// </summary>
public sealed class WorkspaceOptions
{
    public const string SectionName = "Workspaces";

    public int PersonalDailyAiEvaluations { get; set; } = 30;

    public int PersonalMaxCvs { get; set; } = 100;
}