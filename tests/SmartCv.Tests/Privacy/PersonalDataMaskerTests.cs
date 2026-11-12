using SmartCv.Core.Privacy;

namespace SmartCv.Tests;

/// <summary>
/// A37: che thông tin cá nhân trước khi gửi Gemini. 39 tình huống của gói hướng dẫn A37; đổi namespace về SmartCv.Tests theo quy ước
/// của dự án. Một đầu vào của ChuaBietTen_KhongDoanBay được thay (xem ghi chú tại đó) vì mâu thuẫn với yêu cầu che tên dưới chức danh.
/// Tình huống bổ sung: PersonalDataMaskerEdgeCaseTests, PersonalDataMaskerNameDetectionTests.
/// </summary>
public class PersonalDataMaskerTests
{
    [Theory]
    [InlineData("SĐT: 0912 345 678.")]
    [InlineData("SĐT: 0912.345.678")]
    [InlineData("SĐT: 0912-345-678")]
    [InlineData("SĐT: 0912345678")]
    [InlineData("SĐT: 091 234 5678")]
    [InlineData("SĐT: +84 912 345 678")]
    [InlineData("SĐT: +84912345678")]
    [InlineData("SĐT: (+84) 912 345 678")]
    [InlineData("SĐT: 84-912-345-678")]
    [InlineData("SĐT: 0388 123 456")]
    [InlineData("Bàn: 028 3823 4567")]
    [InlineData("Bàn: (028) 3823 4567")]
    [InlineData("Phone: +1 (415) 555-2671")]
    [InlineData("Phone: +65 9123 4567")]
    public void CheSoDienThoai(string text)
    {
        var r = PersonalDataMasker.Mask(text);

        Assert.Equal(1, r.Phones);
        Assert.Contains("[PHONE]", r.Text);
        Assert.DoesNotContain("345", r.Text.Replace("[PHONE]", ""));
        Assert.DoesNotContain("555", r.Text);
        Assert.DoesNotContain("9123", r.Text);
        Assert.DoesNotContain("3823", r.Text);
    }

    [Theory]
    [InlineData("Đại học Bách Khoa (2019 - 2023), GPA 3.2/4")]
    [InlineData("Thực tập 06/2025 - nay, lương 1.500.000 VND")]
    [InlineData("Điểm trung bình 8.0/10, 0.5 năm kinh nghiệm")]
    [InlineData("Từ 03.2021 đến 05.2022 tại FPT")]
    [InlineData("Mã sinh viên 20194567, IELTS 6.5")]
    [InlineData("Dự án 2 năm: .NET 8, C# 12, SQL Server 2022")]
    public void KhongCheSoKhongPhaiDienThoai(string text)
    {
        var r = PersonalDataMasker.Mask(text);

        Assert.Equal(0, r.Total);
        Assert.Equal(text, r.Text);
    }

    [Theory]
    [InlineData("Email: an.nguyen99@gmail.com", "Email: [EMAIL]")]
    [InlineData("mail: tran_binh+cv@fpt.edu.vn;", "mail: [EMAIL];")]
    public void CheEmail(string text, string expected)
    {
        Assert.Equal(expected, PersonalDataMasker.Mask(text).Text);
    }

    [Theory]
    [InlineData("https://www.linkedin.com/in/nguyen-van-an-123")]
    [InlineData("linkedin.com/in/annguyen")]
    [InlineData("facebook.com/an.nguyen.99")]
    [InlineData("http://fb.com/annguyen")]
    [InlineData("github.com/annguyen/smart-cv")]
    public void CheLinkMangXaHoi(string link)
    {
        var r = PersonalDataMasker.Mask($"Liên hệ: {link} (cá nhân)");
        Assert.Equal("Liên hệ: [LINK] (cá nhân)", r.Text);
        Assert.Equal(1, r.Links);
    }

    [Fact]
    public void KhongCheTenCongNgheGitHub()
    {
        const string text = "Dùng GitHub Actions và Git để triển khai.";
        Assert.Equal(text, PersonalDataMasker.Mask(text).Text);
    }

    [Fact]
    public void CheHoTen_CoDau_InHoa_VaMatDau()
    {
        var text = "NGUYỄN VĂN AN\nBackend Developer\n...Tôi là Nguyễn Văn An. Liên hệ NGUYEN VAN AN.";
        var r = PersonalDataMasker.Mask(text, "Nguyễn Văn An");

        Assert.Equal(3, r.Names);
        Assert.Equal("[CANDIDATE]\nBackend Developer\n...Tôi là [CANDIDATE]. Liên hệ [CANDIDATE].", r.Text);
    }

    [Fact]
    public void KhongCheTenMotChu_HoacTuNamTrongTuKhac()
    {
        Assert.Equal("An toàn thông tin", PersonalDataMasker.Mask("An toàn thông tin", "An").Text);
        Assert.Equal("Nguyễn Văn Anh", PersonalDataMasker.Mask("Nguyễn Văn Anh", "Nguyễn Văn An").Text);
    }

    [Fact]
    public void CheNgaySinh_GioiTinh_GiuNhan()
    {
        var r = PersonalDataMasker.Mask("Ngày sinh: 12/03/2002\nGiới tính: Nữ\nNăm sinh 2002");

        Assert.Equal("Ngày sinh: [DOB]\nGiới tính: [GENDER]\nNăm sinh [DOB]", r.Text);
        Assert.Equal(2, r.BirthDates);
        Assert.Equal(1, r.Genders);
    }

    [Fact]
    public void CvDayDu_CheMoiThu_GiuNoiDungChuyenMon()
    {
        const string cv = """
            NGUYỄN VĂN AN
            Email: an.nguyen@gmail.com | SĐT: 0912.345.678 | github.com/annguyen
            KINH NGHIỆM: Backend Developer tại ABC (06/2025 - nay), C#, ASP.NET Core, SQL Server.
            HỌC VẤN: Đại học Bách Khoa 2019 - 2023, GPA 3.2/4.
            """;

        var r = PersonalDataMasker.Mask(cv, "Nguyễn Văn An");

        Assert.Equal(4, r.Total);
        Assert.DoesNotContain("gmail", r.Text);
        Assert.DoesNotContain("0912", r.Text);
        Assert.DoesNotContain("annguyen", r.Text);
        Assert.DoesNotContain("NGUYỄN", r.Text);
        Assert.Contains("06/2025 - nay", r.Text);
        Assert.Contains("2019 - 2023", r.Text);
        Assert.Contains("GPA 3.2/4", r.Text);
        Assert.Contains("ASP.NET Core", r.Text);
    }

    [Theory]
    [InlineData("NGUYỄN THẢO VY\nLập trình viên Frontend", "[CANDIDATE]\nLập trình viên Frontend")]
    [InlineData("\n  Phạm Quang Huy  \nBackend .NET\nLiên hệ Phạm Quang Huy", "\n  [CANDIDATE]  \nBackend .NET\nLiên hệ [CANDIDATE]")]
    public void ChuaBietTen_DoanTuDongDau(string text, string expected)
    {
        Assert.Equal(expected, PersonalDataMasker.Mask(text).Text);
    }

    // Tình huống đầu của gói là "Lập trình viên Backend .NET\nNguyễn Văn An" với kỳ vọng KHÔNG che tên. Theo yêu cầu sửa A37
    // (tên ở dòng dưới chức danh phải được che), kỳ vọng đó chính là lỗ hổng cần sửa: đầu vào ấy chuyển sang
    // PersonalDataMaskerNameDetectionTests.Ten_duoi_chuc_danh_duoc_che (kỳ vọng che tên, giữ chức danh). Ở đây giữ ý định gốc
    // "dòng đầu có dấu chấm thì không bị coi là họ tên" với dòng dưới không phải tên.
    [Theory]
    [InlineData("Lập trình viên Backend .NET\nKinh nghiệm 2 năm C#")]   // dòng đầu có dấu chấm
    [InlineData("Kỹ sư phần mềm\nabc")]                          // viết thường
    [InlineData("CV\nabc")]                                      // một chữ
    [InlineData("Kinh nghiệm 2 năm C# và SQL Server tại công ty ABC")]
    public void ChuaBietTen_KhongDoanBay(string text)
    {
        Assert.Equal(0, PersonalDataMasker.Mask(text).Names);
    }

    [Fact]
    public void NoiDungRong_KhongLoi()
    {
        Assert.Equal("", PersonalDataMasker.Mask(null).Text);
        Assert.Equal(0, PersonalDataMasker.Mask("", "Nguyễn Văn An").Total);
    }
}
