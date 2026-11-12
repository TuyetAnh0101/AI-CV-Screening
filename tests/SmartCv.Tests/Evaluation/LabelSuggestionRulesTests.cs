using SmartCv.Core.Constants;
using SmartCv.Core.Evaluation;
using Xunit;

namespace SmartCv.Tests.A36;

/// <summary>A36: đề xuất nhãn từ quyết định của HR.</summary>
public class LabelSuggestionRulesTests
{
    [Theory]
    [InlineData(ApplicationStages.Shortlisted)]
    [InlineData(ApplicationStages.Interview)]
    public void QuaVongCv_PhongVan_DeXuatDat(string stage)
    {
        Assert.Equal(true, LabelSuggestionRules.Suggest(stage, null));
    }

    [Theory]
    [InlineData(RejectReasons.MissingSkills)]
    [InlineData(RejectReasons.NotEnoughExperience)]
    [InlineData(RejectReasons.OtherRequirement)]
    public void LoaiVi_LyDoChuyenMon_DeXuatKhongDat(string reason)
    {
        Assert.Equal(false, LabelSuggestionRules.Suggest(ApplicationStages.Rejected, reason));
    }

    [Theory]
    [InlineData(RejectReasons.CandidateWithdrew)]   // ứng viên rút: KHÔNG được hiện
    [InlineData(RejectReasons.PositionFilled)]
    [InlineData(RejectReasons.Duplicate)]
    public void LoaiVi_LyDoKhongChuyenMon_KhongDeXuat(string reason)
    {
        Assert.Null(LabelSuggestionRules.Suggest(ApplicationStages.Rejected, reason));
    }

    [Theory]
    [InlineData(ApplicationStages.New)]
    [InlineData(ApplicationStages.Reviewing)]
    [InlineData(ApplicationStages.OnHold)]
    public void ChuaKetLuan_KhongDeXuat(string stage)
    {
        Assert.Null(LabelSuggestionRules.Suggest(stage, null));
    }

    [Fact]
    public void ChuaCoNhan_LaNew()
    {
        Assert.Equal(LabelSuggestionStatus.New, LabelSuggestionRules.Compare(true, null));
    }

    [Fact]
    public void NhanGiongDeXuat_LaSame()
    {
        Assert.Equal(LabelSuggestionStatus.Same, LabelSuggestionRules.Compare(false, false));
    }

    [Fact]
    public void NhanKhacDeXuat_LaConflict()
    {
        // HR từng gán Đạt, nay hồ sơ bị Loại vì thiếu kỹ năng → đề xuất Không đạt → xung đột, tô vàng
        var suggested = LabelSuggestionRules.Suggest(ApplicationStages.Rejected, RejectReasons.MissingSkills)!.Value;
        Assert.Equal(LabelSuggestionStatus.Conflict, LabelSuggestionRules.Compare(suggested, currentLabel: true));
    }

    [Fact]
    public void LyDo_GhiRoGiaiDoanVaLyDoLoai()
    {
        Assert.Equal("Loại: Thiếu kỹ năng bắt buộc", LabelSuggestionRules.Explain(ApplicationStages.Rejected, RejectReasons.MissingSkills));
        Assert.Equal("Phỏng vấn", LabelSuggestionRules.Explain(ApplicationStages.Interview, null));
    }
}
