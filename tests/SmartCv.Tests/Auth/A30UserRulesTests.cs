using SmartCv.Core.Auth;
using Xunit;

namespace SmartCv.Tests.Auth;

/// <summary>A30: tạo / sửa tài khoản, không tự khóa mình, không gỡ Admin cuối cùng, mật khẩu tạm.</summary>
public class A30UserRulesTests
{
    private const int Me = 1;
    private static readonly UserSnapshot MeAdmin = new(Me, UserRoles.Admin, true);
    private static readonly UserSnapshot OtherAdmin = new(2, UserRoles.Admin, true);
    private static readonly UserSnapshot Hr = new(3, UserRoles.Hr, true);

    [Fact]
    public void TaoHopLe_ChuanHoa()
    {
        var (input, errors) = UserRules.ValidateCreate(" Lan.HR@CongTy.vn ", " Trần  Lan ", "HR", null);
        Assert.Empty(errors);
        Assert.Equal("lan.hr@congty.vn", input!.Email);
        Assert.Equal("Trần Lan", input.FullName);
    }

    [Fact]
    public void TaoSai_BaoTungTruong()
    {
        var (_, errors) = UserRules.ValidateCreate("abc", "", "Boss", "123");
        Assert.True(errors.ContainsKey("email"));
        Assert.True(errors.ContainsKey("fullName"));
        Assert.True(errors.ContainsKey("role"));
        Assert.True(errors.ContainsKey("password"));
    }

    [Fact]
    public void VaiTroPhanBietHoaThuong() => Assert.True(UserRules.ValidateCreate("a@b.vn", "A", "hr", null).Errors.ContainsKey("role"));

    [Fact]
    public void KhongTuDoiVaiTroMinh() => Assert.Contains("chính mình", UserRules.CanChangeRole(Me, MeAdmin, UserRoles.Hr, 2));

    [Fact]
    public void KhongHaAdminCuoiCung() => Assert.Contains("cuối cùng", UserRules.CanChangeRole(Me, OtherAdmin, UserRoles.Hr, 1));

    [Fact]
    public void HaAdminKhacKhiConAdmin_DuocPhep() => Assert.Null(UserRules.CanChangeRole(Me, OtherAdmin, UserRoles.Hr, 2));

    [Fact]
    public void NangHrLenAdmin_DuocPhep() => Assert.Null(UserRules.CanChangeRole(Me, Hr, UserRoles.Admin, 1));

    [Fact]
    public void GiuNguyenVaiTro_LuonDuoc() => Assert.Null(UserRules.CanChangeRole(Me, MeAdmin, UserRoles.Admin, 1));

    [Fact]
    public void KhongTuKhoaMinh() => Assert.Contains("chính mình", UserRules.CanLock(Me, MeAdmin, 2));

    [Fact]
    public void KhongKhoaAdminCuoiCung() => Assert.Contains("cuối cùng", UserRules.CanLock(Me, OtherAdmin, 1));

    [Fact]
    public void KhoaHr_DuocPhep() => Assert.Null(UserRules.CanLock(Me, Hr, 1));

    [Fact]
    public void MatKhauTam_DatQuyTac_KhongNhamKyTu()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 200; i++)
        {
            var p = UserRules.GenerateTemporaryPassword();
            Assert.Equal(12, p.Length);
            Assert.Null(PasswordRules.Validate(p));
            Assert.True(p.Any(char.IsUpper) && p.Any(char.IsLower));
            Assert.False(p.IndexOfAny(['0', 'O', '1', 'l', 'I']) >= 0);
            seen.Add(p);
        }
        Assert.Equal(200, seen.Count);   // không lặp lại
    }

    [Fact]
    public void TenVaiTro() => Assert.Equal("Nhân sự (HR)", UserRoles.DisplayName("HR"));
}
