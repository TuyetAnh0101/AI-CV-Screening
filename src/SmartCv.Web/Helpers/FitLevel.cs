namespace SmartCv.Web.Helpers;

/// <summary>
/// 4 mức đánh giá theo tài liệu tổng quan (mục 5): từ 85 Rất phù hợp; từ 70 Phù hợp; từ 55 Tiềm năng; dưới 55 Chưa phù hợp.
/// B2 dựng sẵn; B7–B8 dùng để tô màu bảng xếp hạng: <span class="fit-badge @FitLevel.CssClass(label)">@label</span>
/// Màu khai báo trong wwwroot/app.css (các biến --fit-...).
/// </summary>
public static class FitLevel
{
    public const string Excellent = "Rất phù hợp";
    public const string Good = "Phù hợp";
    public const string Potential = "Tiềm năng";
    public const string Poor = "Chưa phù hợp";

    public static readonly IReadOnlyList<string> All = [Excellent, Good, Potential, Poor];

    public static string FromScore(decimal? score) => score switch
    {
        null => string.Empty,
        >= 85 => Excellent,
        >= 70 => Good,
        >= 55 => Potential,
        _ => Poor
    };

    public static string CssClass(string? label) => label switch
    {
        Excellent => "fit-excellent",
        Good => "fit-good",
        Potential => "fit-potential",
        Poor => "fit-poor",
        _ => "fit-none"
    };
}
