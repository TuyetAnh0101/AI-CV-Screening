using SmartCv.Core.Candidates;
using Xunit;

namespace SmartCv.Tests.Candidates;

/// <summary>A24: điền sẵn thông tin ứng viên từ chữ trong CV.</summary>
public class CandidateInfoExtractorTests
{
    [Fact]
    public void DocDuTenEmailSo()
    {
        var s = CandidateInfoExtractor.Suggest("NGUYỄN VĂN AN\nLập trình viên .NET\nEmail: An.Nguyen@Gmail.com | ĐT: 0912 345 678\n");
        Assert.Equal("Nguyễn Văn An", s.FullName);
        Assert.Equal("an.nguyen@gmail.com", s.Email);
        Assert.Equal("0912345678", s.PhoneNumber);
    }

    [Fact]
    public void SoCoMaNuoc()
    {
        var s = CandidateInfoExtractor.Suggest("Trần Thị Bình\nPhone: +84 987 654 321");
        Assert.Equal("Trần Thị Bình", s.FullName);
        Assert.Equal("0987654321", s.PhoneNumber);
        Assert.Null(s.Email);
    }

    [Fact]
    public void DongDauKhongPhaiTen_DeTrong()
    {
        var s = CandidateInfoExtractor.Suggest("CURRICULUM VITAE 2026 - SOFTWARE ENGINEER POSITION\nabc@xyz.com");
        Assert.Null(s.FullName);
        Assert.Equal("abc@xyz.com", s.Email);
    }

    [Fact]
    public void ChuRong()
    {
        var s = CandidateInfoExtractor.Suggest(null);
        Assert.Null(s.FullName); Assert.Null(s.Email); Assert.Null(s.PhoneNumber);
    }
}
