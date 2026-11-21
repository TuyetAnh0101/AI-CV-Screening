namespace SmartCv.Core.Workspaces;

public static class WorkspaceKinds
{
    /// <summary>Workspace nội bộ chứa toàn bộ dữ liệu có trước A51 (WorkspaceId = 1).</summary>
    public const int InternalWorkspaceId = 1;

    /// <summary>Giá trị mặc định của entity mới, chưa gán workspace; SmartCvDbContext đóng dấu workspace hiện hành khi lưu.</summary>
    public const int UnassignedWorkspaceId = -1;

    public const string Internal = "Internal";
    public const string Personal = "Personal";

    public static readonly IReadOnlyList<string> All = [Internal, Personal];
}
