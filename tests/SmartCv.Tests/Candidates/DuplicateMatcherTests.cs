using SmartCv.Core.Candidates;
using Xunit;

namespace SmartCv.Tests.Candidates;

/// <summary>A24: nhận ra ứng viên nộp lại.</summary>
public class DuplicateMatcherTests
{
    private static readonly ExistingCandidate[] Existing =
    [
        new(1, "Nguyễn Văn An", "an@mail.com", "0912345678"),
        new(2, "Nguyễn Văn An", "an.khac@mail.com", "0988888888"),   // trùng TÊN, khác người
        new(3, "Lê Thị Cúc", "AN@MAIL.COM", null),                    // email cũ viết hoa
        new(4, "Phạm Dũng", null, "0912345678"),
    ];

    [Fact]
    public void TrungEmail_KhongPhanBietHoaThuong()
    {
        var m = DuplicateMatcher.Find("An@Mail.com", null, Existing);
        Assert.Equal([1, 3], m.Select(x => x.CandidateId));
        Assert.All(m, x => Assert.Contains(DuplicateMatcher.Email, x.MatchedOn));
    }

    [Fact]
    public void TrungSo_SauChuanHoa()
    {
        var m = DuplicateMatcher.Find(null, "+84 912 345 678", Existing);
        Assert.Equal([1, 4], m.Select(x => x.CandidateId));
    }

    [Fact]
    public void TrungCaHai_LenDau()
    {
        var m = DuplicateMatcher.Find("an@mail.com", "0912345678", Existing);
        Assert.Equal(1, m[0].CandidateId);
        Assert.Equal(2, m[0].MatchedOn.Count);
        Assert.Equal(3, m.Count);
    }

    [Fact]
    public void TrungTen_KhongTinhLaTrung()
    {
        var m = DuplicateMatcher.Find("moi@mail.com", "0900000000", Existing);
        Assert.Empty(m);
    }

    [Fact]
    public void BoQuaChinhNguoiDangSua()
    {
        var m = DuplicateMatcher.Find("an@mail.com", null, Existing, excludeCandidateId: 1);
        Assert.Equal([3], m.Select(x => x.CandidateId));
    }

    [Fact]
    public void KhongCoEmailVaSo_KhongSo()
    {
        Assert.Empty(DuplicateMatcher.Find(null, null, Existing));
        Assert.Empty(DuplicateMatcher.Find("khong-phai-email", "123", Existing));
    }
}
