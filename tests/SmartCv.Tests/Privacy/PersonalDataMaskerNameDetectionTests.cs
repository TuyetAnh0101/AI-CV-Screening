using SmartCv.Core.Privacy;

namespace SmartCv.Tests;

/// <summary>
/// A37 (vòng 2): nhận diện họ tên khi dòng đầu CV là chức danh, khi tên và chức danh cùng dòng, khi tên viết khác thứ tự,
/// và lưới an toàn khi không xác định được tên. Đồng thời chặn che nhầm chức danh, mục CV, tổ chức, địa danh.
/// Mọi họ tên, email, số điện thoại ở đây là dữ liệu giả.
/// </summary>
public class PersonalDataMaskerNameDetectionTests
{
    // ---------- Dòng đầu là chức danh, tên ở dòng dưới ----------

    [Theory]
    [InlineData("Lập trình viên Backend .NET\nNguyễn Văn An", "Lập trình viên Backend .NET\n[CANDIDATE]")]   // đầu vào cũ của gói
    [InlineData("Backend Developer\nTRẦN MINH KHOA\nKinh nghiệm 2 năm", "Backend Developer\n[CANDIDATE]\nKinh nghiệm 2 năm")]
    [InlineData("Senior Software Engineer\nPhạm Quang Huy\nTôi là Phạm Quang Huy.", "Senior Software Engineer\n[CANDIDATE]\nTôi là [CANDIDATE].")]
    [InlineData("Kỹ Sư Phần Mềm\nLê Thu Hà", "Kỹ Sư Phần Mềm\n[CANDIDATE]")]
    [InlineData("SƠ YẾU LÝ LỊCH\nBackend Developer\nĐặng Thành Trúc", "SƠ YẾU LÝ LỊCH\nBackend Developer\n[CANDIDATE]")]
    [InlineData("Backend Developer\nKhoa Minh Trần\nKinh nghiệm", "Backend Developer\n[CANDIDATE]\nKinh nghiệm")]           // họ ở cuối
    [InlineData("Backend Developer\nKhoa Tran\nKinh nghiệm 2 năm", "Backend Developer\n[CANDIDATE]\nKinh nghiệm 2 năm")]  // không dấu
    [InlineData("Backend Developer\nTRẦN MINH KHOA\nBackend Developer tại ABC", "Backend Developer\n[CANDIDATE]\nBackend Developer tại ABC")]
    public void Ten_duoi_chuc_danh_duoc_che(string text, string expected)
    {
        var r = PersonalDataMasker.Mask(text);

        Assert.Equal(expected, r.Text);
        Assert.Equal(NameSource.Header, r.NameSource);
    }

    // ---------- Tên và chức danh cùng một dòng ----------

    [Theory]
    [InlineData("NGUYỄN VĂN AN - Backend Developer\nKinh nghiệm 2 năm", "[CANDIDATE] - Backend Developer\nKinh nghiệm 2 năm")]
    [InlineData("Trần Minh Khoa | Lập trình viên .NET\nKỹ năng: C#", "[CANDIDATE] | Lập trình viên .NET\nKỹ năng: C#")]
    [InlineData("Backend Developer – Lâm Quỳnh Chi", "Backend Developer – [CANDIDATE]")]
    [InlineData("Phan Đình Nam • Hà Nội • 0912 345 678", "[CANDIDATE] • Hà Nội • [PHONE]")]
    [InlineData("Đặng Thành Trúc, Backend Developer", "[CANDIDATE], Backend Developer")]
    public void Ten_va_chuc_danh_cung_dong(string text, string expected)
    {
        Assert.Equal(expected, PersonalDataMasker.Mask(text).Text);
    }

    // ---------- Biết tên (bảng Candidates), CV viết khác thứ tự ----------

    [Theory]
    [InlineData("Tôi là Khoa Trần Minh, lập trình viên.", "Trần Minh Khoa", "Tôi là [CANDIDATE], lập trình viên.")]
    [InlineData("Contact: Khoa Tran (English CV)", "Trần Minh Khoa", "Contact: [CANDIDATE] (English CV)")]
    [InlineData("Tham chiếu: anh Trần Khoa hỗ trợ.", "Trần Minh Khoa", "Tham chiếu: anh [CANDIDATE] hỗ trợ.")]
    [InlineData("KHOA MINH TRAN\nBackend", "Trần Minh Khoa", "[CANDIDATE]\nBackend")]
    [InlineData("Nguyễn An làm Backend. Liên hệ An Nguyễn.", "An Nguyễn", "[CANDIDATE] làm Backend. Liên hệ [CANDIDATE].")]
    public void Ten_biet_truoc_viet_khac_thu_tu_van_duoc_che(string text, string candidate, string expected)
    {
        var r = PersonalDataMasker.Mask(text, candidate);

        Assert.Equal(expected, r.Text);
        Assert.Equal(NameSource.Provided, r.NameSource);
    }

    [Theory]
    [InlineData("Dự án Thành Công tại ABC, triển khai thành công hệ thống", "Nguyễn Thành Công")]   // tên đệm + tên, chữ thường
    [InlineData("Khoa Công nghệ thông tin, trưởng khoa Trần Văn Bình", "Trần Minh Khoa")]          // "Khoa" của trường, "khoa Trần" viết thường
    [InlineData("Nguyễn Văn Anh", "Nguyễn Văn An")]                                                 // người khác
    public void Biet_ten_khong_che_nham_cum_tu_thuong(string text, string candidate)
    {
        var r = PersonalDataMasker.Mask(text, candidate);

        Assert.Equal(0, r.Names);
        Assert.Equal(text, r.Text);
    }

    // ---------- Mục nội dung, chức danh không bao giờ là họ tên ----------

    [Theory]
    [InlineData("KINH NGHIỆM\nBackend tại FPT, 2 năm kinh nghiệm C#")]
    [InlineData("KỸ NĂNG\nC#, SQL Server, Docker")]
    [InlineData("HỌC VẤN: Đại học Bách Khoa (2019 - 2023)")]
    [InlineData("THÔNG TIN\nBackend Developer")]
    [InlineData("ĐÀO TẠO\nKhóa học .NET nâng cao")]
    public void Muc_noi_dung_khong_bi_coi_la_ho_ten(string text)
    {
        var r = PersonalDataMasker.Mask(text);

        Assert.Equal(0, r.Names);
        Assert.Equal(text, r.Text);
    }

    // ---------- Lưới an toàn khi không xác định được tên ----------

    [Theory]
    [InlineData("Kinh nghiệm 2 năm C#.\nTôi là Phạm Quang Huy, sinh sống tại Hà Nội.", "Kinh nghiệm 2 năm C#.\nTôi là [CANDIDATE], sinh sống tại Hà Nội.")]
    [InlineData("KINH NGHIỆM\nBackend tại FPT\nNguyễn Văn An", "KINH NGHIỆM\nBackend tại FPT\n[CANDIDATE]")]
    [InlineData("Tóm tắt: lập trình viên 3 năm.\nCảm ơn, Ứng viên Trần Thị Bích Ngọc", "Tóm tắt: lập trình viên 3 năm.\nCảm ơn, Ứng viên [CANDIDATE]")]
    public void Luoi_an_toan_che_ten_khi_khong_xac_dinh_duoc_tu_phan_dau(string text, string expected)
    {
        var r = PersonalDataMasker.Mask(text);

        Assert.Equal(expected, r.Text);
        Assert.Equal(NameSource.Fallback, r.NameSource);
    }

    [Theory]
    [InlineData("Học tại Đại học Tôn Đức Thắng, THPT Lê Quý Đôn")]
    [InlineData("Văn phòng ở đường Nguyễn Huệ, TP. Hồ Chí Minh")]
    [InlineData("Thực tập tại Bệnh viện Bạch Mai Hà Nội")]
    [InlineData("Môn học: Lý Thuyết Đồ Thị, Cơ Sở Dữ Liệu")]
    [InlineData("Đoàn Thanh Niên Cộng Sản Hồ Chí Minh")]
    [InlineData("Lương Mong Muốn: 15.000.000 VND")]
    [InlineData("Dự án Ứng Dụng Quản Lý Kho")]
    [InlineData("Hợp tác với đối tác Hàn Quốc và Thái Lan")]
    [InlineData("Backend Developer\nHà Nội\nKinh nghiệm 2 năm")]
    [InlineData("Backend Developer\nCông Ty ABC\nKinh nghiệm 2 năm")]
    public void Khong_che_nham_to_chuc_dia_danh_cum_tu_viet_hoa(string text)
    {
        var r = PersonalDataMasker.Mask(text);

        Assert.Equal(0, r.Names);
        Assert.Equal(text, r.Text);
        Assert.Equal(NameSource.None, r.NameSource);
    }

    // ---------- Nguồn họ tên ----------

    [Theory]
    [InlineData("Tôi là Trần Minh Khoa", "Trần Minh Khoa", NameSource.Provided)]
    [InlineData("Họ và tên: Trần Minh Khoa", null, NameSource.Label)]
    [InlineData("TRẦN MINH KHOA\nBackend", null, NameSource.Header)]
    [InlineData("Kinh nghiệm 2 năm.\nTôi là Trần Minh Khoa.", null, NameSource.Fallback)]
    [InlineData("Backend Developer\nKinh nghiệm 2 năm", null, NameSource.None)]
    public void Ghi_nhan_nguon_ho_ten(string text, string? candidate, NameSource expected)
    {
        Assert.Equal(expected, PersonalDataMasker.Mask(text, candidate).NameSource);
    }
}
