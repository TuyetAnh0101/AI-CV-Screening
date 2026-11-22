namespace SmartCv.Web.Helpers;

/// <summary>B55: kiểm tra nhanh tại UI; API A57 vẫn quyết định cuối cùng.</summary>
public static class AccountProfileRules
{
    public const int MaxLength = 150;
    public static string DisplayRole(string? role) => role switch
    {
        "Admin" => "Quản trị viên",
        "HR" => "HR nội bộ",
        _ => "Người dùng"
    };
    public static string Normalize(string? name) => (name ?? "").Trim();
    public static string? Validate(string? name)
    {
        var value = Normalize(name);
        return value.Length is < 1 or > MaxLength || value.Any(ch => char.IsControl(ch) && !char.IsWhiteSpace(ch))
            ? "Họ tên phải có từ 1 đến 150 ký tự hợp lệ."
            : null;
    }
}
