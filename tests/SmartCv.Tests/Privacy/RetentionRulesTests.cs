using SmartCv.Core.Constants;
using SmartCv.Core.Privacy;
using Xunit;

namespace SmartCv.Tests.Privacy;

/// <summary>A38: hạn lưu khi đổi giai đoạn.</summary>
public class RetentionRulesTests
{
    private static readonly DateTime Changed = new(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(ApplicationStages.Rejected)]
    [InlineData(ApplicationStages.OnHold)]
    public void LoaiHoacDuPhong_HanLuu_CongThemSoThang(string stage)
    {
        var until = RetentionRules.RetainUntilAfterStageChange(stage, Changed, months: 6, currentRetainUntil: null);
        Assert.Equal(new DateTime(2027, 5, 12, 3, 0, 0, DateTimeKind.Utc), until);
    }

    [Theory]
    [InlineData(ApplicationStages.Reviewing)]
    [InlineData(ApplicationStages.Shortlisted)]
    [InlineData(ApplicationStages.Interview)]
    public void GiaiDoanKhac_GiuNguyenHanCu(string stage)
    {
        var old = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(old, RetentionRules.RetainUntilAfterStageChange(stage, Changed, 6, old));
        Assert.Null(RetentionRules.RetainUntilAfterStageChange(stage, Changed, 6, null));
    }

    [Fact]
    public void CuoiThang_AddMonths_KhongTraNgaySai()
    {
        // 31/08 + 6 tháng = 28/02 (không có 31/02)
        var until = RetentionRules.RetainUntilAfterStageChange(ApplicationStages.Rejected,
            new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc), 6, null);
        Assert.Equal(new DateTime(2027, 2, 28, 0, 0, 0, DateTimeKind.Utc), until);
    }

    [Fact]
    public void CvNopNhieuJob_GiuHanMuonHon()
    {
        var later = new DateTime(2027, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(later, RetentionRules.RetainUntilAfterStageChange(ApplicationStages.Rejected, Changed, 6, later));
    }
}
