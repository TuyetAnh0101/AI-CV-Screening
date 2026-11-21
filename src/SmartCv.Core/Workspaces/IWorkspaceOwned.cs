namespace SmartCv.Core.Workspaces;

/// <summary>Entity gốc thuộc đúng một workspace. A52 sẽ đóng dấu và lọc theo giá trị này.</summary>
public interface IWorkspaceOwned
{
    int WorkspaceId { get; set; }
}
