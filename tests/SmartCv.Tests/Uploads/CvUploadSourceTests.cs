using SmartCv.Core.Constants;
using SmartCv.Core.Uploads;
using Xunit;

namespace SmartCv.Tests.Uploads;

/// <summary>A38: thiếu xác nhận đồng ý hoặc thiếu nguồn → từ chối cả lần tải.</summary>
public class CvUploadSourceTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void ThieuDongY_BiTuChoi(bool? consent)
    {
        var (source, title, error) = CvUploadSource.Create(CvSourceChannels.Email, consent, Now);
        Assert.Null(source);
        Assert.Equal("Chưa xác nhận đồng ý", title);
        Assert.Contains("consentConfirmed", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Facebook")]
    [InlineData("email")]   // phân biệt hoa thường, giống CHECK trong database
    public void NguonSai_BiTuChoi(string? channel)
    {
        var (source, title, _) = CvUploadSource.Create(channel, true, Now);
        Assert.Null(source);
        Assert.Equal("Thiếu nguồn CV", title);
    }

    [Fact]
    public void DayDu_GhiNguonVaThoiDiemDongY()
    {
        var (source, _, error) = CvUploadSource.Create(" JobBoard ", true, Now);
        Assert.Null(error);
        Assert.Equal(new CvUploadSource(CvSourceChannels.JobBoard, Now), source);
    }
}
