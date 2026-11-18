using SmartCv.Core.Candidates;
using Xunit;

namespace SmartCv.Tests.Candidates;

/// <summary>A24: kiểm tra số điện thoại 10 số, định dạng email, họ tên.</summary>
public class CandidateRulesTests
{
    [Theory]
    [InlineData("0912345678", "0912345678")]
    [InlineData("0912 345 678", "0912345678")]
    [InlineData("0912.345.678", "0912345678")]
    [InlineData("091-234-5678", "0912345678")]
    [InlineData("+84 912 345 678", "0912345678")]
    [InlineData("84912345678", "0912345678")]
    [InlineData("(028) 3823 4567", "02838234567")]   // 11 số → không hợp lệ (xem dưới)
    public void ChuanHoaDienThoai(string input, string expected)
    {
        var p = CandidateRules.NormalizePhone(input);
        if (expected.Length == 10) Assert.Equal(expected, p);
        else Assert.Null(p);
    }

    [Theory]
    [InlineData("091234567")]      // 9 số
    [InlineData("09123456789")]    // 11 số
    [InlineData("1912345678")]     // không bắt đầu bằng 0
    [InlineData("09123abc78")]
    public void DienThoaiSai(string input) => Assert.Null(CandidateRules.NormalizePhone(input));

    [Theory]
    [InlineData("An.Nguyen@Gmail.com", "an.nguyen@gmail.com")]
    [InlineData("  hr+cv@cong-ty.vn ", "hr+cv@cong-ty.vn")]
    public void ChuanHoaEmail(string input, string expected) => Assert.Equal(expected, CandidateRules.NormalizeEmail(input));

    [Theory]
    [InlineData("an.nguyen")]
    [InlineData("an@")]
    [InlineData("an@gmail")]
    [InlineData("an @gmail.com")]
    [InlineData("An <an@gmail.com>")]
    public void EmailSai(string input) => Assert.Null(CandidateRules.NormalizeEmail(input));

    [Fact]
    public void HopLe_TraDuLieuDaChuanHoa()
    {
        var (input, errors) = CandidateRules.Validate("  Nguyễn   Văn An ", "AN@MAIL.COM", "+84 912 345 678");
        Assert.Empty(errors);
        Assert.Equal("Nguyễn Văn An", input!.FullName);
        Assert.Equal("an@mail.com", input.Email);
        Assert.Equal("0912345678", input.PhoneNumber);
    }

    [Fact]
    public void ThieuTen_VaSaiSo_BaoTungTruong()
    {
        var (input, errors) = CandidateRules.Validate(" ", "an@mail.com", "12345");
        Assert.Null(input);
        Assert.True(errors.ContainsKey("fullName"));
        Assert.True(errors.ContainsKey("phoneNumber"));
        Assert.False(errors.ContainsKey("email"));
    }

    [Fact]
    public void KhongEmailKhongSo_Loi()
    {
        var (_, errors) = CandidateRules.Validate("Trần Thị Bình", null, "  ");
        Assert.True(errors.ContainsKey("contact"));
    }

    [Fact]
    public void ChiCoSo_HopLe()
    {
        var (input, _) = CandidateRules.Validate("Trần Thị Bình", null, "0987654321");
        Assert.Null(input!.Email);
    }

    [Fact]
    public void TenQuaDai_Loi()
    {
        var (_, errors) = CandidateRules.Validate(new string('a', 151), "a@b.com", null);
        Assert.True(errors.ContainsKey("fullName"));
    }
}
