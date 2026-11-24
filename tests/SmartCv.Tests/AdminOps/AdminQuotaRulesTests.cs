using SmartCv.Api.Ops;

namespace SmartCv.Tests.AdminOps;

public sealed class AdminQuotaRulesTests
{
    [Theory]
    [InlineData(0, 0, "Incident")]
    [InlineData(10000, 100000, "Capacity")]
    [InlineData(25, 500, "Trial")]
    [InlineData(3, 3, "Correction")]
    public void Accepts_boundaries_and_documented_reasons(int ai, int cvs, string reason) =>
        Assert.Null(AdminQuotaRules.Validate(ai, cvs, reason));

    [Theory]
    [InlineData(-1, 10, "Capacity")]
    [InlineData(10001, 10, "Capacity")]
    [InlineData(1, -1, "Capacity")]
    [InlineData(1, 100001, "Capacity")]
    [InlineData(1, 1, "other")]
    [InlineData(1, 1, "")]
    public void Rejects_invalid_quota_or_unapproved_reason(int ai, int cvs, string reason) =>
        Assert.NotNull(AdminQuotaRules.Validate(ai, cvs, reason));
}
