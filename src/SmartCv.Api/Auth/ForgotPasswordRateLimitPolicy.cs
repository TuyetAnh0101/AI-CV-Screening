namespace SmartCv.Api.Auth;

/// <summary>
/// A57: 10 yêu cầu / giờ / IP / instance; 3 / giờ / email có thật được kiểm soát trong SQL EmailOutbox.
/// Hạ tầng nhiều API instance cần bổ sung edge/distributed rate limiter.
/// </summary>
public sealed class ForgotPasswordRateLimitPolicy()
    : PublicAuthRateLimitPolicy(10, TimeSpan.FromHours(1), "forgot-password");
