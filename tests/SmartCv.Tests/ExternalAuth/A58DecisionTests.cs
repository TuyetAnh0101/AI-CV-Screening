using System.Security.Claims;
using SmartCv.Core.Auth;

namespace SmartCv.Tests.ExternalAuth;

public sealed class A58DecisionTests
{
    private static ExternalIdentity Identity(bool verified = true, string? email = "test@example.invalid") =>
        new("google", "verified-google-sub", email, verified, "Google Test User");

    [Fact]
    public void Row01_LinkedActiveNonAdmin_CanLogin()
    {
        var d = ExternalDecisionRules.Decide(Identity(), "login", null, 17, true, false, 17, false);
        Assert.Equal(ExternalAction.Login, d.Action);
        Assert.Equal(17, d.UserId);
    }

    [Fact]
    public void Row02_LockedLinkedUser_IsBlocked() =>
        Assert.Equal("account_locked", ExternalDecisionRules.Decide(Identity(), "login", null, 17, false, false, 17, true).ErrorCode);

    [Fact]
    public void Row03_AdminLinkedUser_IsBlocked() =>
        Assert.Equal("admin_not_allowed", ExternalDecisionRules.Decide(Identity(), "login", null, 17, true, true, 17, true).ErrorCode);

    [Fact]
    public void Row04_NewVerifiedGoogle_WhenRegistrationEnabled_CanRegister() =>
        Assert.Equal(ExternalAction.Register, ExternalDecisionRules.Decide(Identity(), "login", null, null, false, false, null, true).Action);

    [Fact]
    public void Row05_DisabledRegistration_BlocksOnlyNewUsers() =>
        Assert.Equal("registration_disabled", ExternalDecisionRules.Decide(Identity(), "login", null, null, false, false, null, false).ErrorCode);

    [Fact]
    public void Row06_SameEmailNeverAutoLinks() =>
        Assert.Equal("link_required", ExternalDecisionRules.Decide(Identity(), "login", null, null, false, false, 9, true).ErrorCode);

    [Theory]
    [InlineData(false, "test@example.invalid")]
    [InlineData(true, null)]
    public void Row07_MissingOrUnverifiedEmailCannotLogin(bool verified, string? email) =>
        Assert.Equal("email_unverified", ExternalDecisionRules.Decide(Identity(verified,email), "login", null, null, false, false, null, true).ErrorCode);

    [Fact]
    public void Row08_InvalidSubCannotEnterFlow() =>
        Assert.Equal("provider_error", ExternalDecisionRules.Decide(Identity() with { Subject = "" }, "login", null, null, false, false, null, true).ErrorCode);

    [Fact]
    public void Row09_ValidTicketCanLinkWithoutEmailClaim() =>
        Assert.Equal(ExternalAction.Link, ExternalDecisionRules.Decide(Identity(false, null), "link", 12, null, false, false, null, false, true).Action);

    [Fact]
    public void Row10_SubLinkedToAnotherUserIsRejected() =>
        Assert.Equal("already_linked", ExternalDecisionRules.Decide(Identity(), "link", 12, 77, true, false, null, true, true).ErrorCode);

    [Fact]
    public void Row11_LastLoginMethodMayNotBeRemoved()
    {
        // The pure rule for unlink is enforced by the service's HasPassword/OtherLogin DB queries.
        Assert.Equal("last_login_method", ExternalUnlinkRules.Check(hasPassword: false, otherLinkedMethods: 0));
        Assert.Null(ExternalUnlinkRules.Check(hasPassword: true, otherLinkedMethods: 0));
        Assert.Null(ExternalUnlinkRules.Check(hasPassword: false, otherLinkedMethods: 1));
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/login")]
    public void UnsafeReturnUrlIsReplacedWithDashboard(string value) =>
        Assert.Equal("/dashboard", ExternalProviders.SafeReturnUrl(value));

    [Fact]
    public void SafeReturnUrlKeepsLocalRouteAndQuery() =>
        Assert.Equal("/jobs?status=active", ExternalProviders.SafeReturnUrl("/jobs?status=active"));

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void EmailVerifiedClaimIsStrict(string? raw, bool expected)
    {
        var list = new List<Claim> { new("sub", "provider-sub"), new("email", "user@example.invalid") };
        if (raw is not null) list.Add(new("email_verified", raw));
        var identity = ExternalIdentity.FromClaims("google", new ClaimsPrincipal(new ClaimsIdentity(list, "OIDC")));
        Assert.Equal(expected, identity.EmailVerified);
    }
}
