using SmartCv.Core.Candidates;
using SmartCv.Core.Constants;
using Xunit;

namespace SmartCv.Tests.Candidates;

/// <summary>A43: kế hoạch xóa dữ liệu theo yêu cầu ứng viên.</summary>
public class CandidateErasePlanTests
{
    [Fact]
    public void RutMoiHoSoChuaLoai_GiuHoSoDaLoai()
    {
        var plan = CandidateErasePlan.Withdrawals(
        [
            (1, ApplicationStages.New),
            (2, ApplicationStages.Interview),
            (3, ApplicationStages.Rejected),
            (4, ApplicationStages.OnHold),
        ]);
        Assert.Equal([1, 2, 4], plan.Select(p => p.ApplicationId));
        Assert.Equal(ApplicationStages.Interview, plan[1].FromStage);
    }

    [Fact]
    public void ChuaXacNhan_Loi()
    {
        var e = CandidateErasePlan.Validate(false, "hr.lan", null);
        Assert.True(e.ContainsKey("confirm"));
    }

    [Fact]
    public void ThieuNguoiXuLy_Loi() =>
        Assert.True(CandidateErasePlan.Validate(true, " ", null).ContainsKey("handledBy"));

    [Fact]
    public void HopLe() => Assert.Empty(CandidateErasePlan.Validate(true, "hr.lan", "Email ứng viên gửi 10/12"));

    [Fact]
    public void TomTat_CoLyDoTheoYeuCauUngVien()
    {
        var s = CandidateErasePlan.Summary(2, 1, 1, true);
        Assert.StartsWith("Theo yêu cầu ứng viên", s);
        Assert.Contains("ẩn danh hóa 2 CV", s);
        Assert.Contains("rút 1 hồ sơ", s);
        Assert.Contains("xóa thông tin liên hệ", s);
    }
}
