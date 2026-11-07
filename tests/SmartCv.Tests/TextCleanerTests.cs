using System.Text;
using SmartCv.Core.Extraction;

namespace SmartCv.Tests;

/// <summary>A7: làm sạch chữ đọc được từ CV.</summary>
public class TextCleanerTests
{
    [Fact]
    public void Normalizes_vietnamese_to_nfc()
    {
        // "Kiểm thử" viết bằng ký tự tổ hợp (chữ cái + dấu rời) như nhiều PDF xuất ra
        var decomposed = "Kiểm thử".Normalize(NormalizationForm.FormD);
        Assert.NotEqual("Kiểm thử", decomposed);

        var cleaned = TextCleaner.Clean(decomposed);

        Assert.Equal("Kiểm thử", cleaned);
        Assert.True(cleaned.IsNormalized(NormalizationForm.FormC));
    }

    [Fact]
    public void Removes_control_chars_and_collapses_whitespace()
    {
        var raw = "  Họ\u0000 tên:\t\tAn\u00A0Nguyễn \r\n\r\n\r\n\r\nKỹ\u00ADnăng – C# — SQL\u0007  ";

        Assert.Equal("Họ tên: An Nguyễn\n\nKỹnăng - C# - SQL", TextCleaner.Clean(raw));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData(" \n\t ", "")]
    public void Empty_input_returns_empty(string? raw, string expected) =>
        Assert.Equal(expected, TextCleaner.Clean(raw));

    [Fact]
    public void Counts_only_non_whitespace_chars() =>
        Assert.Equal(6, TextCleaner.CountMeaningfulChars(" ab c\n d\tef "));
}
