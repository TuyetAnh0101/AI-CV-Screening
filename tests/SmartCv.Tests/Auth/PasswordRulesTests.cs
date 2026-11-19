using SmartCv.Core.Auth;

namespace SmartCv.Tests.Auth;

public class PasswordRulesTests
{
    [Theory]
    [InlineData("a1b2c3d4")]
    [InlineData("Tuyển dụng 2026")]
    [InlineData("ABC12345")]
    public void Valid_passwords_allow_phrases_and_do_not_require_symbols(string password) =>
        Assert.Null(PasswordRules.Validate(password, "admin@example.invalid"));

    [Theory]
    [InlineData(null, "bắt buộc")]
    [InlineData("", "bắt buộc")]
    [InlineData("short1", "ít nhất")]
    [InlineData("onlyletters", "chữ và số")]
    [InlineData("12345678", "chữ và số")]
    [InlineData(" password123", "khoảng trắng")]
    [InlineData("password123 ", "khoảng trắng")]
    public void Invalid_passwords_report_only_the_rule(string? password, string expected) =>
        Assert.Contains(expected, PasswordRules.Validate(password));

    [Fact]
    public void Both_length_boundaries_are_enforced()
    {
        Assert.Null(PasswordRules.Validate(new string('a', 127) + "1"));
        Assert.Contains("tối đa 128", PasswordRules.Validate(new string('a', 128) + "1"));
        Assert.Null(PasswordRules.Validate("abcdefg1"));
        Assert.Contains("ít nhất 8", PasswordRules.Validate("abcdef1"));
    }

    [Fact]
    public void Email_comparison_ignores_case_and_email_padding() =>
        Assert.Contains("trùng email", PasswordRules.Validate("admin1@example.invalid", " ADMIN1@EXAMPLE.INVALID "));
}
