namespace SmartCv.Core.Auth;

public static class SmartCvClaims
{
    public const string UserId = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Role = "role";
    public const string Stamp = "smartcv_stamp";
    public const string MustChangePassword = "smartcv_mcp";
    /// <summary>WorkspaceId mà token được cấp cho. Mỗi request được xác nhận lại với database (UserSessionValidator).</summary>
    public const string Workspace = "smartcv_ws";
}
