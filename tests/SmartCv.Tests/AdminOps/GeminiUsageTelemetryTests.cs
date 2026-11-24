using Microsoft.Extensions.Configuration;
using SmartCv.Infrastructure.Ai;

namespace SmartCv.Tests.AdminOps;

public sealed class GeminiUsageTelemetryTests
{
    [Fact]
    public void Parses_all_usage_metadata_without_reading_candidate_content()
    {
        const string body = """
            {"candidates":[{"content":{"parts":[{"text":"sensitive response"}]}}],
             "usageMetadata":{"promptTokenCount":1500,"candidatesTokenCount":200,
               "thoughtsTokenCount":70,"cachedContentTokenCount":300,"totalTokenCount":1770}}
            """;
        var result = Assert.IsType<GeminiTokenUsage>(GeminiUsageParser.Parse(body));
        Assert.Equal(1500, result.PromptTokens);
        Assert.Equal(200, result.CandidateTokens);
        Assert.Equal(70, result.ThoughtsTokens);
        Assert.Equal(300, result.CachedTokens);
        Assert.Equal(1770, result.TotalTokens);
        Assert.DoesNotContain("sensitive", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"usageMetadata\":{}}")]
    public void Missing_or_invalid_metadata_does_not_become_zero(string body) =>
        Assert.Null(GeminiUsageParser.Parse(body));

    [Fact]
    public void Missing_specific_fields_stay_null()
    {
        var result = Assert.IsType<GeminiTokenUsage>(GeminiUsageParser.Parse(
            "{\"usageMetadata\":{\"totalTokenCount\":0}}"));
        Assert.Equal(0, result.TotalTokens);
        Assert.Null(result.PromptTokens);
        Assert.Null(result.CandidateTokens);
    }

    [Fact]
    public void Negative_fractional_or_out_of_range_values_are_not_accepted()
    {
        var result = GeminiUsageParser.Parse(
            "{\"usageMetadata\":{\"promptTokenCount\":-7,\"candidatesTokenCount\":1.7," +
            "\"totalTokenCount\":9999999999999999}}");
        Assert.Null(result);
    }

    [Fact]
    public void Telemetry_source_is_scoped_and_restored_without_cross_request_bleed()
    {
        Assert.Equal("Unknown", GeminiTelemetryScope.Snapshot.Source);
        using (GeminiTelemetryScope.Begin("Screening", 10, 3))
        {
            Assert.Equal(("Screening", (int?)10, (int?)3), GeminiTelemetryScope.Snapshot);
            using (GeminiTelemetryScope.Begin("Preview"))
                Assert.Equal("Preview", GeminiTelemetryScope.Snapshot.Source);
            Assert.Equal("Screening", GeminiTelemetryScope.Snapshot.Source);
        }
        Assert.Equal("Unknown", GeminiTelemetryScope.Snapshot.Source);
    }

    [Fact]
    public void Cost_is_null_without_verified_prices_not_misrepresented_as_zero()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var result = GeminiCostEstimate.TryEstimate(config, "test-model",
            new GeminiTokenUsage(1000, 200, null, null, 1200));
        Assert.Null(result.CostUsd);
    }

    [Fact]
    public void Cost_uses_explicit_model_rates_cached_discount_and_thoughts()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiTelemetry:Pricing:test-model:InputUsdPerMillion"] = "2.00",
            ["AiTelemetry:Pricing:test-model:OutputUsdPerMillion"] = "8.00",
            ["AiTelemetry:Pricing:test-model:CachedInputUsdPerMillion"] = "0.50",
            ["AiTelemetry:Pricing:test-model:PriceVersion"] = "test-approved-prices-2026-11"
        }).Build();
        var value = GeminiCostEstimate.TryEstimate(config, "test-model",
            new GeminiTokenUsage(1000, 200, 50, 400, 1250));
        // (600*2 + 400*0.5 + (200+50)*8) / 1e6 = 0.0034 USD
        Assert.Equal(0.0034m, value.CostUsd);
        Assert.Equal("test-approved-prices-2026-11", value.Description);
    }

    [Fact]
    public void Bad_price_configuration_does_not_break_telemetry_capture()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiTelemetry:Pricing:m:InputUsdPerMillion"] = "INVALID",
            ["AiTelemetry:Pricing:m:OutputUsdPerMillion"] = "2.0",
            ["AiTelemetry:Pricing:m:PriceVersion"] = "test-version"
        }).Build();
        var result = GeminiCostEstimate.TryEstimate(config, "m",
            new GeminiTokenUsage(300, 200, null, null, 500));
        Assert.Null(result.CostUsd);
    }
}
