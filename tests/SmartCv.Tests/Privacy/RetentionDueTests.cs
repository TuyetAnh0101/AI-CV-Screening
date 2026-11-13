using SmartCv.Core.Constants;
using SmartCv.Core.Privacy;
using Xunit;

namespace SmartCv.Tests.Privacy;

/// <summary>A39: điều kiện tự ẩn danh hóa (tiến trình nền) và chặn ẩn danh hóa thủ công.</summary>
public class RetentionDueTests
{
    private static readonly DateTime Changed = new(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DaQuaHan_KhongConHoSoDangXet_DenHan()
    {
        var now = Changed.AddMonths(7);
        Assert.True(RetentionRules.IsDueForAnonymization(Changed.AddMonths(6), null, [ApplicationStages.Rejected], now));
    }

    [Fact]
    public void ChuaQuaHan_ChuaDenHan()
    {
        Assert.False(RetentionRules.IsDueForAnonymization(Changed.AddMonths(6), null, [ApplicationStages.Rejected], Changed.AddMonths(5)));
    }

    [Fact]
    public void QuaHan_NhungJobKhacDangPhongVan_ChuaDenHan()
    {
        Assert.False(RetentionRules.IsDueForAnonymization(Changed, null,
            [ApplicationStages.Rejected, ApplicationStages.Interview], Changed.AddYears(1)));
    }

    [Fact]
    public void DaAnDanh_HoacKhongCoHan_KhongLamLai()
    {
        Assert.False(RetentionRules.IsDueForAnonymization(Changed, Changed, [ApplicationStages.Rejected], Changed.AddYears(1)));
        Assert.False(RetentionRules.IsDueForAnonymization(null, null, [ApplicationStages.Rejected], Changed.AddYears(1)));
    }

    [Theory]
    [InlineData(ApplicationStages.Shortlisted)]
    [InlineData(ApplicationStages.Interview)]
    public void ThuCong_ChanKhiQuaVongCvHoacPhongVan(string stage)
    {
        var reason = RetentionRules.ManualBlockReason([ApplicationStages.Rejected, stage]);
        Assert.NotNull(reason);
        Assert.Contains(ApplicationStages.DisplayName(stage), reason);
    }

    [Fact]
    public void ThuCong_LoaiHoacMoi_DuocPhep()
    {
        Assert.Null(RetentionRules.ManualBlockReason([ApplicationStages.Rejected, ApplicationStages.New]));
        Assert.Null(RetentionRules.ManualBlockReason([]));
    }
}
