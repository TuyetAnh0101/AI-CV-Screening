extern alias web;
using web::SmartCv.Web.Helpers;

namespace SmartCv.Tests;

public sealed class B56QuotaMessageTests
{
    [Theory]
    [InlineData(429,"ai_quota_exceeded")]
    [InlineData(409,"cv_quota_exceeded")]
    [InlineData(409,"no_active_prompt")]
    public void Known_codes_are_explicit(int status, string code) =>
        Assert.True(B56QuotaRules.Recognized(status, code));

    [Theory]
    [InlineData(429,"PreviewQuotaExhausted")]
    [InlineData(401,"ai_quota_exceeded")]
    [InlineData(503,"cv_quota_exceeded")]
    public void Other_errors_are_not_rebranded(int status, string code) =>
        Assert.False(B56QuotaRules.Recognized(status, code));

    [Fact]
    public void Ai_message_uses_actual_quota_and_vietnam_reset()
    {
        var reset = new DateTimeOffset(2026,11,23,17,0,0,TimeSpan.Zero);
        var text = B56QuotaRules.Message(429,"ai_quota_exceeded",30,30,reset);
        Assert.Contains("30/30",text);
        Assert.Contains("00:00",text);
    }

    [Fact]
    public void Unknown_limit_is_not_guessed()
    {
        var text = B56QuotaRules.Message(409,"cv_quota_exceeded");
        Assert.DoesNotContain("100 CV",text);
    }
}
