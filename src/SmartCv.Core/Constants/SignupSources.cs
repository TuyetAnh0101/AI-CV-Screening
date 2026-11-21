namespace SmartCv.Core.Constants;

/// <summary>Nguồn tạo tài khoản (Users.SignupSource). Giá trị nhà cung cấp ngoài là tên chữ thường của Provider.</summary>
public static class SignupSources
{
    public const string Admin = "Admin";
    public const string Local = "Local";
    public const string Google = "google";
    public const string LinkedIn = "linkedin";

    public static readonly IReadOnlyList<string> All = [Admin, Local, Google, LinkedIn];
}
