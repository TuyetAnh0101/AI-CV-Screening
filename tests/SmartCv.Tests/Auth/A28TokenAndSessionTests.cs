using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmartCv.Api.Auth;
using SmartCv.Core.Auth;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;

namespace SmartCv.Tests.Auth;

public class A28TokenAndSessionTests
{
    private static User Account() => new() { UserId = 7, Email = "synthetic@example.invalid", FullName = "Synthetic account", Role = "HR", PasswordHash = "SYNTHETIC_HASH_NEVER_IN_TOKEN" };
    private static ClaimsPrincipal Principal(User user) => new(new ClaimsIdentity(new JsonWebToken(A28TestSupport.Token(user)).Claims, "Test"));

    [Fact]
    public void Claims_helpers_keep_the_future_actor_limit_and_do_not_grant_admin_to_an_anonymous_identity()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("name", new string('x', 99) + "😀more"), new Claim("role", "Admin")], "Test"));
        Assert.True(principal.IsAdmin()); Assert.Equal("Admin", principal.GetRole());
        Assert.Equal(99, principal.ActorName().Length);
        Assert.False(new ClaimsPrincipal(new ClaimsIdentity([new Claim("role", "Admin")])).IsAdmin());
        Assert.Equal("synthetic@example.invalid", new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", "synthetic@example.invalid")])).ActorName());
    }

    [Theory]
    [InlineData("Admin", false)]
    [InlineData("HR", false)]
    [InlineData("Admin", true)]
    [InlineData("HR", true)]
    public async Task Issued_JWT_has_exact_contract_role_and_eight_hour_UTC_lifetime(string role, bool mustChange)
    {
        var user = Account(); user.Role = role; user.MustChangePassword = mustChange;
        var options = A28TestSupport.Settings();
        var result = new JwtTokenService(Options.Create(options), TimeProvider.System).Create(user);
        var jwt = new JsonWebToken(result.Token);
        Assert.Equal(SecurityAlgorithms.HmacSha256, jwt.Alg);
        Assert.Equal(28800, jwt.ValidTo.Subtract(jwt.IssuedAt).TotalSeconds);
        Assert.Equal(DateTimeKind.Utc, result.ExpiresAt.Kind);
        Assert.Equal(jwt.ValidTo, result.ExpiresAt);
        Assert.Equal(user.UserId.ToString(), jwt.GetClaim("sub").Value);
        Assert.Equal(role, jwt.GetClaim("role").Value);
        Assert.Equal(mustChange ? "true" : "false", jwt.GetClaim(SmartCvClaims.MustChangePassword).Value);
        Assert.Equal(new[] { "aud", "email", "exp", "iat", "iss", "name", "nbf", "role", "smartcv_mcp", "smartcv_stamp", "sub" }, jwt.Claims.Select(c => c.Type).Order());
        var payload = System.Text.Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(jwt.EncodedPayload));
        Assert.DoesNotContain(user.PasswordHash, payload);
        Assert.DoesNotContain(options.Key!, payload);
        var validated = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(result.Token, JwtTokenService.ValidationParameters(options));
        Assert.True(validated.IsValid);
        Assert.Equal(user.FullName, validated.ClaimsIdentity.Name);
        Assert.True(new ClaimsPrincipal(validated.ClaimsIdentity).IsInRole(role));
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("audience-slash")]
    [InlineData("type")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("unsigned")]
    [InlineData("algorithm")]
    [InlineData("no_expiry")]
    [InlineData("tampered")]
    public async Task Validation_rejects_invalid_tokens(string problem)
    {
        var options = A28TestSupport.Settings();
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = problem == "issuer" ? "wrong-issuer" : options.Issuer,
            Audience = problem == "audience" ? "wrong-audience" : problem == "audience-slash" ? options.Audience + "/" : options.Audience,
            TokenType = problem == "type" ? "different-token-type" : "JWT",
            IssuedAt = now.AddHours(-2),
            NotBefore = problem == "future" ? now.AddMinutes(3) : now.AddHours(-2),
            Expires = problem == "expired" ? now.AddMinutes(-3) : problem == "no_expiry" ? null : now.AddHours(8),
            Claims = new Dictionary<string, object> { ["sub"] = "7", ["role"] = "HR" },
            SigningCredentials = problem == "unsigned" ? null : new(JwtTokenService.SigningKey(problem == "signature" ? A28TestSupport.Settings(A28TestSupport.Password().PadRight(64, 'x')) : options), problem == "algorithm" ? SecurityAlgorithms.HmacSha384 : SecurityAlgorithms.HmacSha256)
        };
        var token = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
        if (problem == "tampered")
        {
            var parts = token.Split('.');
            parts[1] = Base64UrlEncoder.Encode("{\"sub\":\"999\",\"role\":\"Admin\"}");
            token = string.Join('.', parts);
        }
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, JwtTokenService.ValidationParameters(options));
        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("missing-key")]
    [InlineData("short-key")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("too-short-life")]
    [InlineData("too-long-life")]
    public void Invalid_options_report_the_field_without_secret(string problem)
    {
        var options = A28TestSupport.Settings();
        if (problem == "missing-key") options.Key = null;
        if (problem == "short-key") options.Key = "synthetic-short";
        if (problem == "issuer") options.Issuer = " ";
        if (problem == "audience") options.Audience = "";
        if (problem == "too-short-life") options.ExpiresHours = 0;
        if (problem == "too-long-life") options.ExpiresHours = 25;
        Assert.NotNull(options.Validate());
        if (options.Key is { } key) Assert.DoesNotContain(key, options.Validate()!);
    }

    [Theory]
    [InlineData(false, "old", "old")]
    [InlineData(true, "new", "old")]
    [InlineData(true, "old", "")]
    [InlineData(true, "", " ")]
    public void Session_rules_reject_inactive_or_invalid_stamp(bool active, string stored, string provided) =>
        Assert.NotNull(SessionRules.Check(new(active, stored), provided));

    [Fact]
    public void Missing_account_rejected_and_valid_state_accepted()
    {
        Assert.NotNull(SessionRules.Check(null, "stamp"));
        Assert.Null(SessionRules.Check(new(true, "stamp"), "stamp"));
    }

    [Theory]
    [InlineData("sub")]
    [InlineData("email")]
    [InlineData("name")]
    [InlineData("role")]
    [InlineData("smartcv_stamp")]
    [InlineData("smartcv_mcp")]
    public async Task Required_claims_cannot_be_missing_or_duplicated(string claimType)
    {
        var user = Account();
        var principal = Principal(user);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var validator = new UserSessionValidator(new Store(new(true, user.SecurityStamp)), cache, TimeProvider.System);
        var identity = (ClaimsIdentity)principal.Identity!;
        var claim = identity.FindFirst(claimType)!;
        identity.RemoveClaim(claim);
        Assert.NotNull(await validator.ValidateAsync(principal, default));
        identity.AddClaim(claim); identity.AddClaim(claim);
        Assert.NotNull(await validator.ValidateAsync(principal, default));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-an-id")]
    public void User_id_must_be_a_positive_integer(string value) =>
        Assert.Null(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", value)])).GetUserId());

    [Fact]
    public async Task Session_cache_expires_in_thirty_seconds_and_can_be_invalidated()
    {
        var user = Account();
        var store = new Store(new(true, user.SecurityStamp));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new A28TestSupport.Clock(DateTimeOffset.UtcNow);
        var validator = new UserSessionValidator(store, cache, clock);
        var principal = Principal(user);
        Assert.Null(await validator.ValidateAsync(principal, default));
        store.State = new(false, User.NewStamp());
        Assert.Null(await validator.ValidateAsync(principal, default));
        Assert.Equal(1, store.Reads);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.NotNull(await validator.ValidateAsync(principal, default));
        Assert.Equal(2, store.Reads);
        store.State = new(true, user.SecurityStamp);
        UserSessionValidator.Invalidate(cache, user.UserId);
        Assert.Null(await validator.ValidateAsync(principal, default));
        Assert.Equal(3, store.Reads);
    }

    [Fact]
    public async Task Database_failure_is_not_cached_as_an_authenticated_session()
    {
        var user = Account();
        var store = new Store(null) { Fail = true };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var validator = new UserSessionValidator(store, cache, TimeProvider.System);
        await Assert.ThrowsAsync<A28TestSupport.SyntheticDatabaseException>(() => validator.ValidateAsync(Principal(user), default));
        store.Fail = false; store.State = new(true, user.SecurityStamp);
        Assert.Null(await validator.ValidateAsync(Principal(user), default));
        Assert.Equal(2, store.Reads);
    }

    private sealed class Store(UserSessionState? state) : ISessionStateStore
    {
        public UserSessionState? State = state;
        public int Reads;
        public bool Fail;
        public Task<UserSessionState?> GetAsync(int userId, CancellationToken ct)
        {
            Reads++;
            return Fail ? throw new A28TestSupport.SyntheticDatabaseException() : Task.FromResult(State);
        }
    }
}
