namespace SmartCv.Api.Auth;

/// <summary>Separate buckets for Google browser redirects and one-time code exchange.</summary>
public sealed class ExternalStartRateLimitPolicy() : PublicAuthRateLimitPolicy(
    20, TimeSpan.FromMinutes(10), "external-start");

public sealed class ExternalExchangeRateLimitPolicy() : PublicAuthRateLimitPolicy(
    10, TimeSpan.FromMinutes(1), "external-exchange");
