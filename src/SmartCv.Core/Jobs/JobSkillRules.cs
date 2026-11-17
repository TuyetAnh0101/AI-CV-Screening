using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;

namespace SmartCv.Core.Jobs;

public static partial class JobSkillRules
{
    [GeneratedRegex(@"\A[A-Z0-9][A-Z0-9_]{1,49}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    public static bool IsValidCode(string? code) => code is not null && CodePattern().IsMatch(code);

    public static string NormalizeCode(string? code)
    {
        var text = (code ?? "").Trim().Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD).ToUpperInvariant();
        var result = new StringBuilder();
        foreach (var c in text)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') result.Append(c);
            else if (result.Length > 0 && result[^1] != '_') result.Append('_');
        }
        return result.ToString().TrimEnd('_');
    }

    public static string SuggestCode(string? name)
    {
        var code = NormalizeCode(name?.Replace("#", "SHARP").Replace("+", "PLUS").Replace(".", ""));
        return code.Length <= 50 ? code : code[..50].TrimEnd('_');
    }

    public static (List<string> Aliases, string? Error) NormalizeAliases(IEnumerable<string?>? input)
    {
        var aliases = (input ?? []).Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => Whitespace().Replace(a!.Trim().Normalize(NormalizationForm.FormC), " ").ToLowerInvariant())
            .Distinct(StringComparer.Ordinal).ToList();
        if (aliases.Count > 30) return ([], "Tối đa 30 từ khóa cho mỗi kỹ năng.");
        if (aliases.Any(a => a.Length > 100)) return ([], "Mỗi từ khóa tối đa 100 ký tự.");
        return (aliases, null);
    }

    public static Dictionary<string, string[]> Validate(string? displayName, string? skillType)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 100)
            errors["displayName"] = ["Tên kỹ năng không được để trống và tối đa 100 ký tự."];
        if (skillType is not (SkillTypes.MustHave or SkillTypes.NiceToHave))
            errors["skillType"] = ["Loại kỹ năng phải là MustHave (Bắt buộc) hoặc NiceToHave (Ưu tiên)."];
        return errors;
    }

    public static (JobSkillInput? Normalized, Dictionary<string, string[]> Errors) ValidateNew(JobSkillInput? input)
    {
        if (input is null) return (null, new() { ["body"] = ["Cần gửi thông tin kỹ năng."] });
        var errors = Validate(input.DisplayName, input.SkillType);
        var code = string.IsNullOrWhiteSpace(input.SkillCode) ? SuggestCode(input.DisplayName) : NormalizeCode(input.SkillCode);
        if (!IsValidCode(code)) errors["skillCode"] = ["Mã kỹ năng gồm 2–50 chữ in hoa không dấu, số hoặc dấu gạch dưới; bắt đầu bằng chữ hoặc số."];
        var (aliases, error) = NormalizeAliases(input.Aliases);
        if (error is not null) errors["aliases"] = [error];
        return errors.Count > 0 ? (null, errors) : (new JobSkillInput
        {
            SkillCode = code, DisplayName = input.DisplayName!.Trim(), SkillType = input.SkillType,
            Aliases = aliases.Select(a => (string?)a).ToList()
        }, errors);
    }
}
