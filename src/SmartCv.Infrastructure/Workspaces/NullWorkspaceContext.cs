using SmartCv.Core.Workspaces;

namespace SmartCv.Infrastructure.Workspaces;

/// <summary>Không có workspace và không phải system scope: mọi truy vấn nghiệp vụ trả 0 dòng, mọi thao tác ghi bị từ chối.</summary>
public sealed class NullWorkspaceContext : IWorkspaceContext
{
    public static readonly NullWorkspaceContext Instance = new();
    public int? WorkspaceId => null;
    public string? Role => null;
    public string? Kind => null;
    public bool IsSystemScope => false;
}
