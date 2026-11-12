using SmartCv.Core.Applications;
using SmartCv.Core.Constants;
using Xunit;

namespace SmartCv.Tests.Applications;

/// <summary>A35: quy tắc đổi giai đoạn hồ sơ (đổi đơn, đổi hàng loạt, Loại thiếu lý do).</summary>
public class StageChangeRulesTests
{
    private const string Hr = "HR - Trần Thu Hà";

    [Fact]
    public void DoiDon_HopLe()
    {
        var (change, error) = StageChangeRules.Validate("Shortlisted", null, "  Đủ kỹ năng  ", Hr);

        Assert.Null(error);
        Assert.NotNull(change);
        Assert.Equal(ApplicationStages.Shortlisted, change!.ToStage);
        Assert.Equal("Đủ kỹ năng", change.Note);   // đã cắt khoảng trắng
    }

    [Fact]
    public void Loai_ThieuLyDo_BaoLoi()
    {
        var (change, error) = StageChangeRules.Validate("Rejected", null, null, Hr);

        Assert.Null(change);
        Assert.Equal("Loại hồ sơ phải chọn lý do.", error);
    }

    [Fact]
    public void Loai_LyDoRong_CungBaoLoi()
    {
        Assert.NotNull(StageChangeRules.Validate("Rejected", "   ", null, Hr).Error);
    }

    [Fact]
    public void Loai_LyDoLa_BaoLoi()
    {
        var (_, error) = StageChangeRules.Validate("Rejected", "TooOld", null, Hr);
        Assert.Contains("không hợp lệ", error);
    }

    [Fact]
    public void Loai_CoLyDo_HopLe()
    {
        var (change, error) = StageChangeRules.Validate("Rejected", "MissingSkills", null, Hr);
        Assert.Null(error);
        Assert.Equal(RejectReasons.MissingSkills, change!.RejectReason);
    }

    [Fact]
    public void GiaiDoanKhac_KemLyDo_BaoLoi()
    {
        var (_, error) = StageChangeRules.Validate("OnHold", "MissingSkills", null, Hr);
        Assert.Equal("Chỉ chọn lý do khi Loại hồ sơ.", error);
    }

    [Theory]
    [InlineData(null, "Chưa chọn giai đoạn mới.")]
    [InlineData("Hired", "không hợp lệ")]
    public void GiaiDoanSai_BaoLoi(string? stage, string expected)
    {
        Assert.Contains(expected, StageChangeRules.Validate(stage, null, null, Hr).Error);
    }

    [Fact]
    public void ThieuNguoiThaoTac_BaoLoi()
    {
        Assert.Equal("Thiếu tên người thao tác.", StageChangeRules.Validate("Reviewing", null, null, " ").Error);
    }

    [Fact]
    public void GhiChuQua500KyTu_BaoLoi()
    {
        Assert.NotNull(StageChangeRules.Validate("Reviewing", null, new string('a', 501), Hr).Error);
    }

    [Fact]
    public void CungGiaiDoan_KhongDoi()
    {
        var change = StageChangeRules.Validate("Shortlisted", null, null, Hr).Change!;
        Assert.True(StageChangeRules.IsNoOp(new CurrentStage(1, "Shortlisted", null), change));
    }

    [Fact]
    public void LoaiSangLoai_DoiLyDo_LaThayDoi()
    {
        var change = StageChangeRules.Validate("Rejected", "NotEnoughExperience", null, Hr).Change!;
        Assert.False(StageChangeRules.IsNoOp(new CurrentStage(1, "Rejected", "MissingSkills"), change));
    }

    [Fact]
    public void MoLaiHoSoDaLoai_DuocPhep()
    {
        var (change, error) = StageChangeRules.Validate("Reviewing", null, "Xem lại theo yêu cầu trưởng nhóm", Hr);
        Assert.Null(error);
        Assert.False(StageChangeRules.IsNoOp(new CurrentStage(1, "Rejected", "MissingSkills"), change!));
    }

    [Fact]
    public void DoiHangLoat_KetQuaTungHoSo_GiuThuTu()
    {
        var change = StageChangeRules.Validate("Shortlisted", null, null, Hr).Change!;
        var current = new Dictionary<int, CurrentStage>
        {
            [1] = new(1, "New", null),
            [2] = new(2, "Shortlisted", null),
            [3] = new(3, "Rejected", "Duplicate")
        };

        var plan = StageChangeRules.Plan([3, 99, 1, 2], current, change);

        Assert.Equal(new[] { 3, 99, 1, 2 }, plan.Select(p => p.ApplicationId));
        Assert.Equal(StageChangeOutcome.Changed, plan[0].Outcome);
        Assert.Equal("Rejected", plan[0].FromStage);
        Assert.Equal(StageChangeOutcome.NotFound, plan[1].Outcome);
        Assert.Equal(StageChangeOutcome.Changed, plan[2].Outcome);
        Assert.Equal(StageChangeOutcome.Unchanged, plan[3].Outcome);
        Assert.Equal(2, plan.Count(p => p.Outcome == StageChangeOutcome.Changed));   // đúng 2 dòng lịch sử sẽ được ghi
    }

    [Fact]
    public void DoiHangLoat_DanhSachSai_BaoLoi()
    {
        Assert.NotNull(StageChangeRules.ValidateIds([]));
        Assert.NotNull(StageChangeRules.ValidateIds([1, 1]));
        Assert.NotNull(StageChangeRules.ValidateIds([0]));
        Assert.NotNull(StageChangeRules.ValidateIds(Enumerable.Range(1, 201).ToList()));
        Assert.Null(StageChangeRules.ValidateIds([1, 2, 3]));
    }

    [Fact]
    public void DanhSachLuaChon_DuSauGiaTriVaNhanTiengViet()
    {
        Assert.Equal(6, ApplicationStages.All.Count);
        Assert.Equal(6, RejectReasons.All.Count);
        Assert.Equal("Qua vòng CV", ApplicationStages.DisplayName("Shortlisted"));
    }
}
