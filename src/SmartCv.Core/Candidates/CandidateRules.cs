using System.Net.Mail;
using System.Text.RegularExpressions;

namespace SmartCv.Core.Candidates;

/// <summary>Thông tin ứng viên đã kiểm tra và chuẩn hóa, sẵn sàng lưu.</summary>
public sealed record CandidateInput(string FullName, string? Email, string? PhoneNumber);

/// <summary>
/// A24: kiểm tra và chuẩn hóa thông tin ứng viên. Hàm thuần → unit test được.
///   - Họ tên: bắt buộc, tối đa 150 ký tự, gộp khoảng trắng thừa.
///   - Email: không bắt buộc; đúng định dạng; lưu chữ thường; tối đa 254 ký tự.
///   - Điện thoại: không bắt buộc; bỏ dấu cách / chấm / gạch / ngoặc; +84 hoặc 84 đầu số → 0;
///     sau chuẩn hóa phải đúng 10 chữ số, bắt đầu bằng 0 (khớp cột CHAR(10) và CHECK chỉ có số).
///   - Phải có ít nhất email HOẶC điện thoại, để còn liên hệ và nhận ra ứng viên nộp lại.
/// </summary>
public static partial class CandidateRules
{
    public const int MaxNameLength = 150;
    public const int MaxEmailLength = 254;

    [GeneratedRegex(@"\A0[0-9]{9}\z")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"[\s.\-()]")]
    private static partial Regex PhoneSeparators();

    [GeneratedRegex(@"\A[^@\s]+@[^@\s]+\.[A-Za-z]{2,}\z", RegexOptions.NonBacktracking)]
    private static partial Regex SimpleEmailRegex();

    public static (CandidateInput? Input, Dictionary<string, string[]> Errors) Validate(string? fullName, string? email, string? phone)
    {
        var errors = new Dictionary<string, string[]>();

        var name = NormalizeName(fullName);
        if (name is null) errors["fullName"] = ["Họ tên là bắt buộc."];
        else if (name.Length > MaxNameLength) errors["fullName"] = [$"Họ tên tối đa {MaxNameLength} ký tự."];

        string? normalizedEmail = null;
        if (!string.IsNullOrWhiteSpace(email))
        {
            normalizedEmail = NormalizeEmail(email);
            if (normalizedEmail is null) errors["email"] = ["Email không đúng định dạng (ví dụ: ten@congty.com)."];
            else if (normalizedEmail.Length > MaxEmailLength) errors["email"] = [$"Email tối đa {MaxEmailLength} ký tự."];
        }

        string? normalizedPhone = null;
        if (!string.IsNullOrWhiteSpace(phone))
        {
            normalizedPhone = NormalizePhone(phone);
            if (normalizedPhone is null) errors["phoneNumber"] = ["Số điện thoại phải gồm đúng 10 chữ số, bắt đầu bằng 0 (ví dụ 0912345678)."];
        }

        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phone))
            errors["contact"] = ["Cần ít nhất email hoặc số điện thoại."];

        return errors.Count > 0
            ? (null, errors)
            : (new CandidateInput(name!, normalizedEmail, normalizedPhone), errors);
    }

    public static string? NormalizeName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return null;
        return string.Join(' ', fullName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Email hợp lệ → chữ thường, bỏ khoảng trắng; không hợp lệ → null.</summary>
    public static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var e = email.Trim();
        if (e.Length > MaxEmailLength) return null;
        if (!SimpleEmailRegex().IsMatch(e)) return null;
        try
        {
            var addr = new MailAddress(e);
            if (addr.Address != e) return null;          // "Tên <a@b.com>" không nhận
        }
        catch (FormatException) { return null; }
        return e.ToLowerInvariant();
    }

    /// <summary>"+84 912.345.678", "(091) 234-5678" → "0912345678"; không đủ 10 số → null.</summary>
    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var p = PhoneSeparators().Replace(phone.Trim(), "");
        if (p.StartsWith("+84")) p = "0" + p[3..];
        else if (p.StartsWith("84") && p.Length == 11) p = "0" + p[2..];
        return PhoneRegex().IsMatch(p) ? p : null;
    }
}
