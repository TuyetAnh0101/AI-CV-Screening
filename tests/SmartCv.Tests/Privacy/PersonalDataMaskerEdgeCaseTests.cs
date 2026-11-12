using System.Diagnostics;
using System.Text;
using SmartCv.Core.Privacy;

namespace SmartCv.Tests;

/// <summary>A37: tình huống bổ sung ngoài 39 tình huống của gói hướng dẫn (Unicode, nhiều định dạng, đoán tên, CV dài/rỗng).</summary>
public class PersonalDataMaskerEdgeCaseTests
{
    // ---------- Unicode tiếng Việt: dựng sẵn / tổ hợp, có dấu / mất dấu, xuống dòng giữa tên ----------

    [Fact]
    public void Ten_trong_CV_dang_to_hop_NFD_van_duoc_che()
    {
        var text = "Nguyễn Văn An".Normalize(NormalizationForm.FormD) + " làm Backend";
        var r = PersonalDataMasker.Mask(text, "Nguyễn Văn An");

        Assert.Equal(1, r.Names);
        Assert.Equal("[CANDIDATE] làm Backend", r.Text);
    }

    [Fact]
    public void Ten_ung_vien_dang_to_hop_NFD_van_khop_CV_dung_san()
    {
        var r = PersonalDataMasker.Mask("Nguyễn Văn An là ứng viên", "Nguyễn Văn An".Normalize(NormalizationForm.FormD));
        Assert.Equal("[CANDIDATE] là ứng viên", r.Text);
    }

    [Theory]
    [InlineData("Tôi là Nguyễn Văn\nAn, lập trình viên", "Tôi là [CANDIDATE], lập trình viên")]   // OCR xuống dòng giữa tên
    [InlineData("Liên hệ nGuYeN vAn An.", "Liên hệ [CANDIDATE].")]                               // hoa thường lẫn lộn, mất dấu
    [InlineData("Tôi là  Nguyễn   Văn  An", "Tôi là  [CANDIDATE]")]                                 // nhiều khoảng trắng
    public void Ten_che_duoc_khi_khac_hoa_thuong_dau_va_khoang_trang(string text, string expected)
    {
        Assert.Equal(expected, PersonalDataMasker.Mask(text, "  Nguyễn   Văn An ").Text);
    }

    [Fact]
    public void Chu_D_gach_va_ten_mat_dau_do_OCR()
    {
        var r = PersonalDataMasker.Mask("ĐẶNG THÀNH TRÚC\nLiên hệ Dang Thanh Truc", "Đặng Thành Trúc");
        Assert.Equal(2, r.Names);
        Assert.Equal("[CANDIDATE]\nLiên hệ [CANDIDATE]", r.Text);
    }

    [Fact]
    public void Khong_che_ten_dai_hon_ten_ung_vien()
    {
        Assert.Equal("Trần Minh Khoai", PersonalDataMasker.Mask("Trần Minh Khoai", "Trần Minh Khoa").Text);
    }

    // ---------- Số điện thoại: thêm định dạng ----------

    [Theory]
    [InlineData("Di động: +84-912-345-678")]
    [InlineData("Di động: (84) 912 345 678")]
    [InlineData("SĐT:0912345678")]
    [InlineData("Gọi 0912345678.")]
    [InlineData("Tel: +44 20 7946 0958")]
    [InlineData("Bàn: 0236.3000.000")]
    [InlineData("Hotline cá nhân: 0388.123.456")]
    public void Che_them_dinh_dang_so_dien_thoai(string text)
    {
        var r = PersonalDataMasker.Mask(text);

        Assert.Equal(1, r.Phones);
        Assert.Contains(PersonalDataMasker.PhoneToken, r.Text);
        Assert.DoesNotMatch(@"\d", r.Text);
    }

    [Fact]
    public void Hai_so_dien_thoai_tren_cung_dong()
    {
        var r = PersonalDataMasker.Mask("0912 345 678 / 0987.654.321");
        Assert.Equal(2, r.Phones);
        Assert.Equal("[PHONE] / [PHONE]", r.Text);
    }

    // ---------- Dữ liệu chuyên môn: không che nhầm ----------

    [Theory]
    [InlineData("Docker 24.0.7, Node.js 20.11.1, Angular 17")]
    [InlineData("Máy chủ 192.168.100.200, cổng 8080")]
    [InlineData("Chứng chỉ AWS SAA-C03, ISO 27001:2022, TOEIC 850/990")]
    [InlineData("Giảm 40% thời gian, xử lý 1.000.000 request/ngày")]
    [InlineData("Điểm thi 27.5/30, khối A00, xếp hạng top 5%")]
    [InlineData("Kinh nghiệm: 3+ năm (06/2021 - 08/2024)")]
    [InlineData("Lương mong muốn 15.000.000 - 20.000.000 VND")]
    [InlineData("Ngân sách dự án +2.000.000 VND")]
    [InlineData("Mã sinh viên 2019456789")]
    [InlineData("CI/CD bằng GitHub Actions, quản lý mã trên GitHub, học qua LinkedIn Learning")]
    [InlineData("Dùng Facebook Graph API và .NET 8, SQL Server 2022")]
    public void Khong_che_so_lieu_va_ten_cong_nghe(string text)
    {
        var r = PersonalDataMasker.Mask(text);

        Assert.Equal(0, r.Total);
        Assert.Equal(text, r.Text);
    }

    // ---------- Đường dẫn ----------

    [Theory]
    [InlineData("Portfolio: https://annguyen.github.io/blog", "Portfolio: [LINK]")]
    [InlineData("Xem linkedin.com/in/an-nguyen.", "Xem [LINK].")]   // giữ dấu chấm cuối câu
    [InlineData("Repo: github.com/an/smartcv, CI bằng GitHub Actions", "Repo: [LINK], CI bằng GitHub Actions")]
    [InlineData("Facebook: facebook.com/an.nguyen.99 | LinkedIn: www.linkedin.com/in/an", "Facebook: [LINK] | LinkedIn: [LINK]")]
    [InlineData("Trang mygithub.com/an không phải GitHub", "Trang mygithub.com/an không phải GitHub")]
    public void Che_duong_dan_mang_xa_hoi_ca_nhan(string text, string expected)
    {
        Assert.Equal(expected, PersonalDataMasker.Mask(text).Text);
    }

    // ---------- Ngày sinh, giới tính ----------

    [Theory]
    [InlineData("Sinh năm: 2001", "Sinh năm: [DOB]")]
    [InlineData("Ngày sinh: 05 tháng 3 năm 2001", "Ngày sinh: [DOB]")]
    [InlineData("Date of birth: 2001-03-05", "Date of birth: [DOB]")]
    [InlineData("DOB: 05/03/2001", "DOB: [DOB]")]
    [InlineData("Born in 2001", "Born in [DOB]")]
    [InlineData("Gender: Female", "Gender: [GENDER]")]
    [InlineData("Giới tính: Nam. Quê quán miền Nam, Việt Nam", "Giới tính: [GENDER]. Quê quán miền Nam, Việt Nam")]
    public void Che_ngay_sinh_gioi_tinh_giu_nhan(string text, string expected)
    {
        Assert.Equal(expected, PersonalDataMasker.Mask(text).Text);
    }

    // ---------- Chưa biết tên ứng viên: đoán ----------

    [Theory]
    [InlineData("SƠ YẾU LÝ LỊCH\nNGUYỄN VĂN AN\nBackend", "SƠ YẾU LÝ LỊCH\n[CANDIDATE]\nBackend")]
    [InlineData("CURRICULUM VITAE\n\nTrần Minh Khoa\nKỹ năng: C#", "CURRICULUM VITAE\n\n[CANDIDATE]\nKỹ năng: C#")]
    [InlineData("LÊ THU HÀ\nKiểm thử phần mềm", "[CANDIDATE]\nKiểm thử phần mềm")]                 // "Thu" là tên, không phải "thử"
    [InlineData("LÂM QUỲNH CHI\nĐịa chỉ: Hà Nội", "[CANDIDATE]\nĐịa chỉ: Hà Nội")]                    // "Chi" là tên, không phải "chỉ"
    [InlineData("PHAN ĐÌNH NAM\nNăm sinh: 2001", "[CANDIDATE]\nNăm sinh: [DOB]")]                      // "Nam" là tên, không phải "năm"
    [InlineData("NGUYỄN VĂN LẬP\nLập trình viên", "[CANDIDATE]\nLập trình viên")]                     // "Lập" là tên
    public void Doan_ten_tu_dong_dau_bo_qua_tieu_de(string text, string expected)
    {
        Assert.Equal(expected, PersonalDataMasker.Mask(text).Text);
    }

    [Theory]
    [InlineData("THÔNG TIN CÁ NHÂN\nHọ và tên: Phan Đình Nam\nNăm sinh: 2001\nGiới tính: Nam",
                "THÔNG TIN CÁ NHÂN\nHọ và tên: [CANDIDATE]\nNăm sinh: [DOB]\nGiới tính: [GENDER]")]
    [InlineData("Họ và tên: Lâm Quỳnh Chi Ngày sinh: 02/02/2002", "Họ và tên: [CANDIDATE] Ngày sinh: [DOB]")]
    [InlineData("Full name: NGUYEN THAO VY | Email: vy@example.com", "Full name: [CANDIDATE] | Email: [EMAIL]")]
    public void Doan_ten_tu_nhan_ho_va_ten(string text, string expected)
    {
        Assert.Equal(expected, PersonalDataMasker.Mask(text).Text);
    }

    [Theory]
    [InlineData("Backend Developer")]
    [InlineData("Lập Trình Viên")]
    [InlineData("Kỹ Sư Phần Mềm")]
    [InlineData("Software Engineer Intern")]
    public void Khong_coi_chuc_danh_dong_dau_la_ho_ten(string title)
    {
        // Chức danh ở dòng đầu phải giữ nguyên ở MỌI chỗ (kể cả khi lặp lại trong phần kinh nghiệm) vì dùng để đánh giá.
        // Tên nằm dưới chức danh vẫn được che: xem PersonalDataMaskerNameDetectionTests.Ten_duoi_chuc_danh_duoc_che.
        var r = PersonalDataMasker.Mask($"{title}\nKinh nghiệm 2 năm\n{title} tại ABC");

        Assert.Equal(0, r.Names);
        Assert.Equal($"{title}\nKinh nghiệm 2 năm\n{title} tại ABC", r.Text);
    }

    [Fact]
    public void Biet_ten_thi_che_ca_khi_ten_khong_o_dong_dau()
    {
        var r = PersonalDataMasker.Mask("Backend Developer\nTôi là Trần Minh Khoa", "Trần Minh Khoa");
        Assert.Equal("Backend Developer\nTôi là [CANDIDATE]", r.Text);
    }

    // ---------- Nhiều loại cùng lúc ----------

    // ReplaceLineEndings: chuỗi raw theo xuống dòng của file nguồn (CRLF khi Git checkout với autocrlf), CV đọc từ PDF dùng LF.
    private static readonly string FullCv = """
        SƠ YẾU LÝ LỊCH
        TRẦN MINH KHOA
        Ngày sinh: 15/08/1999 | Giới tính: Nam
        Email: khoa.tran@example.com | Điện thoại: 0900 123 456 | linkedin.com/in/tran-minh-khoa
        Portfolio: https://tranminhkhoa.github.io/projects
        HỌC VẤN: Đại học Công nghệ (2017 - 2021), GPA 3.30/4.0
        KINH NGHIỆM: Công ty ABC (03/2023 - nay): 30+ API ASP.NET Core trên .NET 8, SQL Server 2022, CI/CD bằng GitHub Actions.
        CHỨNG CHỈ: AWS SAA-C03, IELTS 7.0, TOEIC 850/990
        Tôi là Trần Minh Khoa, sống tại miền Nam, Việt Nam.
        """.ReplaceLineEndings("\n");

    [Fact]
    public void CV_nhieu_loai_thong_tin_ca_nhan_che_dung_so_luong_va_giu_chuyen_mon()
    {
        var r = PersonalDataMasker.Mask(FullCv);

        Assert.Equal((1, 1, 2, 2, 1, 1), (r.Emails, r.Phones, r.Links, r.Names, r.BirthDates, r.Genders));
        Assert.Equal(8, r.Total);
        var lines = r.Text.Split('\n');
        Assert.Equal("SƠ YẾU LÝ LỊCH", lines[0]);
        Assert.Equal("[CANDIDATE]", lines[1]);
        Assert.Equal("Ngày sinh: [DOB] | Giới tính: [GENDER]", lines[2]);
        Assert.Equal("Email: [EMAIL] | Điện thoại: [PHONE] | [LINK]", lines[3]);
        Assert.Equal("Portfolio: [LINK]", lines[4]);
        Assert.Equal(FullCv.Split('\n')[5..8], lines[5..8]);   // học vấn, kinh nghiệm, chứng chỉ: giữ nguyên từng ký tự
        Assert.Equal("Tôi là [CANDIDATE], sống tại miền Nam, Việt Nam.", lines[8]);
        Assert.DoesNotContain("khoa", r.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", r.Text);
        Assert.DoesNotContain("1999", r.Text);
    }

    [Fact]
    public void Che_lai_ban_da_che_khong_doi_gi()
    {
        var once = PersonalDataMasker.Mask(FullCv).Text;
        var twice = PersonalDataMasker.Mask(once);

        Assert.Equal(0, twice.Total);
        Assert.Equal(once, twice.Text);
    }

    // ---------- CV dài, rỗng ----------

    [Fact]
    public void CV_rat_dai_che_du_moi_vi_tri_va_khong_dong_noi_dung_khac()
    {
        const string line = "Kinh nghiệm phát triển API ASP.NET Core với SQL Server 2022, GPA 3.5/4 (2019 - 2023).\n";
        var sb = new StringBuilder("Họ và tên: Trần Minh Khoa\nEmail: khoa.tran@example.com\n");
        for (var i = 0; i < 2500; i++) sb.Append(line);
        sb.Append("SĐT: 0912 345 678\n");
        for (var i = 0; i < 2500; i++) sb.Append(line);
        sb.Append("linkedin.com/in/tran-minh-khoa và Trần Minh Khoa");
        var text = sb.ToString();
        Assert.True(text.Length > 400_000);

        var watch = Stopwatch.StartNew();
        var r = PersonalDataMasker.Mask(text);
        watch.Stop();

        Assert.Equal((1, 1, 1, 2), (r.Emails, r.Phones, r.Links, r.Names));
        Assert.DoesNotContain("khoa", r.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0912", r.Text);
        Assert.Equal(5000, r.Text.Split("GPA 3.5/4 (2019 - 2023)").Length - 1);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Che CV dài mất {watch.Elapsed}");
    }

    [Theory]
    [InlineData("   \n\t ")]
    [InlineData("\n\n")]
    public void CV_chi_co_khoang_trang(string text)
    {
        var r = PersonalDataMasker.Mask(text, null);
        Assert.Equal(0, r.Total);
        Assert.Equal(text, r.Text);
    }
}
