namespace SmartCv.Core.Privacy;

/// <summary>
/// A37: từ điển dùng để nhận diện họ tên trong CV tiếng Việt (tách riêng cho dễ đọc, dễ bổ sung).
/// Quy ước: chữ "đã bỏ dấu" là chữ qua Fold (bỏ dấu, viết thường, đ → d). Cụm nhiều chữ cách nhau đúng một khoảng trắng.
/// Bổ sung từ mới thì thêm test tương ứng trong PersonalDataMaskerNameDetectionTests.
/// </summary>
public static partial class PersonalDataMasker
{
    /// <summary>Tiêu đề chung của cả CV (đã bỏ dấu): bỏ qua khi tìm tên ở phần đầu.</summary>
    private static readonly HashSet<string> DocumentHeadings = new(StringComparer.Ordinal)
    {
        "cv", "resume", "curriculum vitae", "so yeu ly lich", "ly lich", "ly lich ca nhan", "ho so", "ho so xin viec", "ho so ung tuyen",
        "don xin viec", "thong tin ca nhan", "personal information", "personal details", "profile", "about me", "gioi thieu",
        "gioi thieu ban than", "lien he", "thong tin lien he", "contact", "contact information"
    };

    /// <summary>Mục nội dung (đã bỏ dấu): gặp là hết phần đầu CV, không tìm tên tiếp. Không bao giờ coi là họ tên.</summary>
    private static readonly HashSet<string> SectionHeadings = new(StringComparer.Ordinal)
    {
        "kinh nghiem", "kinh nghiem lam viec", "qua trinh lam viec", "work experience", "experience", "professional experience",
        "hoc van", "trinh do hoc van", "education", "ky nang", "skills", "technical skills", "du an", "cac du an", "du an da thuc hien",
        "projects", "chung chi", "certificates", "certifications", "giai thuong", "awards", "so thich", "interests", "hobbies",
        "hoat dong", "activities", "nguoi tham chieu", "tham chieu", "references", "muc tieu", "muc tieu nghe nghiep", "objective",
        "career objective", "summary", "tom tat", "ngoai ngu", "languages"
    };

    /// <summary>
    /// Chữ (đã bỏ dấu) không bao giờ là một phần họ tên: chức danh, mục CV, công nghệ, tổ chức bằng tiếng Anh.
    /// Không đưa chữ tiếng Việt đơn lẻ vào đây vì bỏ dấu dễ trùng tên người ("thử" → Thu, "chỉ" → Chi).
    /// </summary>
    private static readonly HashSet<string> NonNameWords = new(StringComparer.Ordinal)
    {
        "developer", "engineer", "programmer", "analyst", "designer", "tester", "manager", "intern", "internship", "architect",
        "consultant", "specialist", "administrator", "backend", "frontend", "fullstack", "software", "devops", "scientist",
        "officer", "executive", "accountant", "student", "senior", "junior", "fresher", "lead", "leader", "team", "data", "web",
        "mobile", "experience", "education", "skills", "skill", "projects", "project", "certificates", "certifications",
        "references", "awards", "interests", "hobbies", "languages", "summary", "objective", "profile", "contact", "activities",
        "university", "college", "school", "company", "corporation", "group", "ltd", "jsc", "inc", "bank", "server", "core",
        "cloud", "framework", "api", "sql", "java", "python", "javascript", "react", "angular", "docker", "git", "github",
        "linux", "windows", "microsoft", "google", "amazon", "aws", "azure"
    };

    /// <summary>
    /// Cụm 2 chữ (đã bỏ dấu) cho biết đây là chức danh, mục CV, tổ chức hay cụm từ thường, không phải họ tên.
    /// Gồm các cụm bắt đầu bằng chữ trùng họ ("Đào Tạo", "Lương Mong Muốn", "Lưu Trữ", "Lý Thuyết", "Cao Đẳng").
    /// </summary>
    private static readonly string[] NonNamePhrases =
    [
        // chức danh, nghề
        "lap trinh", "ky su", "chuyen vien", "thuc tap", "kiem thu", "phan mem", "du lieu", "nhan vien", "ky thuat", "quan ly",
        "truong phong", "truong nhom", "sinh vien", "giam doc", "cao cap",
        // mục CV
        "kinh nghiem", "hoc van", "ky nang", "du an", "muc tieu", "thong tin", "lien he", "chung chi", "giai thuong", "so thich",
        "hoat dong", "tham chieu", "ban than", "ngoai ngu", "cong viec", "dao tao", "trinh do", "thanh tich", "gioi thieu", "ho so",
        "ly lich",
        // tổ chức, trường
        "cong ty", "dai hoc", "cao dang", "cao hoc", "hoc vien", "trung tam", "ngan hang", "tap doan", "chi nhanh", "pho thong",
        "trung hoc", "doan thanh", "thanh nien", "doan vien", "benh vien", "nong nghiep",
        // cụm từ thường bắt đầu bằng chữ trùng họ
        "ly thuyet", "ly do", "phuong phap", "phuong thuc", "quan he", "thai do", "dong nghiep", "luong thuong", "luong mong",
        "luong co", "muc luong", "luu tru", "tang truong", "chu ky", "kim loai", "lam viec", "ung dung", "dam phan", "nghiem tuc",
        "he thong", "cong nghe", "khach hang"
    ];

    /// <summary>Địa danh (đã bỏ dấu, cả cụm): nhiều tên tỉnh/quận bắt đầu bằng chữ trùng họ ("Hà Nội", "Lâm Đồng", "Hoàng Mai").</summary>
    private static readonly HashSet<string> Places = new(StringComparer.Ordinal)
    {
        "viet nam", "ha noi", "ho chi minh", "thanh pho ho chi minh", "sai gon", "da nang", "hai phong", "can tho", "nha trang",
        "vung tau", "da lat", "quy nhon", "ha long", "bien hoa", "thu duc", "binh duong", "dong nai", "long an", "quang ninh",
        "thai nguyen", "nam dinh", "nghe an", "thanh hoa", "ha tinh", "quang nam", "quang ngai", "khanh hoa", "lam dong",
        "dak lak", "gia lai", "kien giang", "an giang", "ca mau", "vinh long", "tien giang", "ben tre", "tra vinh", "soc trang",
        "bac lieu", "hau giang", "dong thap", "tay ninh", "binh phuoc", "ba ria", "phu yen", "binh dinh", "quang tri", "quang binh",
        "ninh binh", "ha nam", "hung yen", "thai binh", "vinh phuc", "phu tho", "yen bai", "lao cai", "ha giang", "cao bang",
        "lang son", "son la", "dien bien", "lai chau", "hoa binh", "tuyen quang", "bac kan", "bac giang", "bac ninh", "hai duong",
        "kon tum", "dak nong", "ninh thuan", "binh thuan", "ha dong", "hoang mai", "le chan", "ngo quyen", "dong da", "cau giay",
        "thai lan", "han quoc", "trung quoc", "nhat ban", "chau a", "chau au", "dong nam a", "hoa ky", "dai loan"
    };

    /// <summary>
    /// Chữ đứng ngay trước một chuỗi viết hoa (đã bỏ dấu): chuỗi đó là tên trường, tổ chức, giải thưởng, địa chỉ mang tên người,
    /// không phải họ tên ứng viên ("Đại học Tôn Đức Thắng", "THPT Lê Quý Đôn", "đường Nguyễn Huệ", "Bệnh viện Bạch Mai").
    /// </summary>
    private static readonly string[] InstitutionPrefixes =
    [
        "dai hoc", "truong", "thpt", "thcs", "tieu hoc", "hoc vien", "cao dang", "cong ty", "tap doan", "ngan hang", "benh vien",
        "giai thuong", "quy", "bao tang", "san bay", "cong vien", "duong", "pho", "quan", "huyen", "phuong", "xa", "tinh",
        "thanh pho", "tp", "tp.", "khu", "toa nha", "university", "university of", "school", "college", "street", "district",
        "city", "ward"
    ];

    /// <summary>Chữ mở đầu tên tổ chức/địa chỉ, viết thường CÓ dấu: chuỗi bắt đầu bằng các chữ này không phải họ tên.</summary>
    private static readonly string[] InstitutionStarts =
    [
        "trường", "thpt", "thcs", "đại học", "học viện", "cao đẳng", "công ty", "tập đoàn", "ngân hàng", "bệnh viện", "đường", "phố",
        "quận", "huyện", "phường", "xã", "tỉnh", "thành phố", "giải thưởng", "quỹ", "university", "school", "college"
    ];

    /// <summary>
    /// Họ Việt phổ biến, viết thường CÓ dấu. Không đưa vào các họ hiếm mà thường là từ ngữ trong CV
    /// (Từ, Ứng, Phí, Phó, Triệu, Tiêu, Đồng, Tăng, Lại, Vi, Phương, Quan...): chúng dễ làm che nhầm tên dự án, tiêu đề.
    /// </summary>
    private static readonly HashSet<string> Surnames = new(StringComparer.Ordinal)
    {
        "nguyễn", "trần", "lê", "phạm", "hoàng", "huỳnh", "phan", "vũ", "võ", "đặng", "bùi", "đỗ", "hồ", "ngô", "dương", "lý",
        "trương", "đinh", "mai", "hà", "lâm", "lương", "trịnh", "cao", "quách", "tô", "tôn", "tạ", "thái", "đoàn", "lưu", "kiều",
        "khúc", "chu", "vương", "thạch", "ông", "giang", "phùng", "nông", "kim", "hứa", "mạc", "tống", "khổng", "đào", "bạch",
        "bành", "châu", "diệp", "đàm", "hàn", "nghiêm", "ninh", "doãn", "khương", "giáp"
    };

    /// <summary>Họ viết không dấu (CV tiếng Anh, OCR mất dấu) ít trùng từ thường: tính ở mọi độ dài chuỗi.</summary>
    private static readonly HashSet<string> UnaccentedSurnames = new(StringComparer.Ordinal)
    {
        "nguyen", "tran", "pham", "hoang", "huynh", "phan", "dang", "bui", "ngo", "duong", "truong", "dinh", "luong", "trinh",
        "quach", "luu", "kieu", "khuc", "vuong", "thach", "phung", "hua", "khong", "chau", "diep", "nghiem", "khuong", "giap"
    };

    /// <summary>Họ không dấu ngắn, dễ trùng từ thường ("Le", "Do", "Ha", "Mai", "Cao"...): chỉ tính khi chuỗi có từ 3 chữ.</summary>
    private static readonly HashSet<string> AmbiguousUnaccentedSurnames = new(StringComparer.Ordinal)
    {
        "le", "do", "ho", "vu", "vo", "ly", "ha", "to", "ta", "mai", "kim", "chu", "ong", "ton", "tong", "doan", "dao", "dam", "han",
        "bach", "banh", "lam", "cao", "thai", "giang", "nong", "mac", "ninh"
    };

    /// <summary>
    /// Chữ ĐẦU của nhãn trường thông tin, so sánh CÓ DẤU (không bỏ dấu): dừng lấy tên tại đây
    /// ("Họ và tên: Phan Đình Nam Năm sinh: ..."). Giữ dấu để "Năm" khác tên "Nam", "chỉ" khác tên "Chi".
    /// </summary>
    private static readonly HashSet<string> FieldStartWords = new(StringComparer.Ordinal)
    {
        "ngày", "năm", "giới", "email", "sđt", "điện", "số", "địa", "phone", "mobile", "tel", "date", "gender", "address", "dob"
    };
}
