using SmartCv.Core.Auth;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;

namespace SmartCv.Tests.Auth;

public class SeedAdminPlanTests
{
    internal static SeedAdminOptions Valid(string email = " Admin@Example.invalid ") => new()
    {
        Email = email, FullName = "  Quản\ttrị   hệ thống  ", Password = "Synthetic9" + Guid.NewGuid().ToString("N")
    };

    [Fact]
    public void Active_admin_takes_priority_over_invalid_config_and_email_lookup()
    {
        var decision = SeedAdminPlan.Decide(true, new(), _ => throw new Exception("Must not query email"));
        Assert.Equal(SeedAdminAction.AlreadyHasAdmin, decision.Action);
    }

    [Fact]
    public void Missing_or_partial_credentials_warn_as_in_the_guide()
    {
        foreach (var seed in new SeedAdminOptions?[] { null, new(), new() { Email = "a@example.invalid" }, new() { Password = "synthetic-only" } })
            Assert.Equal(SeedAdminAction.MissingConfig, SeedAdminPlan.Decide(false, seed, _ => false).Action);
    }

    [Fact]
    public void Email_and_full_name_are_normalized_and_decision_does_not_hold_password()
    {
        var seed = Valid();
        var d = SeedAdminPlan.Decide(false, seed, _ => false);
        Assert.Equal(SeedAdminAction.Create, d.Action);
        Assert.Equal("admin@example.invalid", d.Email);
        Assert.Equal("Quản trị hệ thống", d.FullName);
        Assert.DoesNotContain(seed.Password!, d.ToString());
        Assert.Equal("admin@example.invalid", new User { Email = seed.Email! }.Email);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Missing_full_name_uses_the_documented_default(string? name)
    {
        var seed = Valid(); seed.FullName = name;
        Assert.Equal("Quản trị hệ thống", SeedAdminPlan.Decide(false, seed, _ => false).FullName);
    }

    [Fact]
    public void Full_name_length_is_checked_after_normalization()
    {
        var seed = Valid(); seed.FullName = " " + new string('x', 150) + " ";
        Assert.Equal(SeedAdminAction.Create, SeedAdminPlan.Decide(false, seed, _ => false).Action);
        seed.FullName = new string('x', 151);
        Assert.Equal(SeedAdminAction.Invalid, SeedAdminPlan.Decide(false, seed, _ => false).Action);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("Name <a@example.invalid>")]
    [InlineData("a b@example.invalid")]
    public void Invalid_email_stops_startup_without_echoing_config(string email)
    {
        var seed = Valid(email);
        var d = SeedAdminPlan.Decide(false, seed, _ => false);
        Assert.Equal(SeedAdminAction.Invalid, d.Action);
        Assert.Contains("Email", d.Message);
        Assert.DoesNotContain(email, d.Message);
    }

    [Fact]
    public void Overlong_email_is_invalid()
    {
        var seed = Valid(new string('a', 245) + "@example.invalid");
        Assert.Equal(SeedAdminAction.Invalid, SeedAdminPlan.Decide(false, seed, _ => false).Action);
    }

    [Fact]
    public void Invalid_password_is_not_in_the_error()
    {
        var seed = Valid(); seed.Password = "bad1";
        var d = SeedAdminPlan.Decide(false, seed, _ => false);
        Assert.Equal(SeedAdminAction.Invalid, d.Action);
        Assert.Contains("Password", d.Message);
        Assert.DoesNotContain(seed.Password, d.Message);
    }

    [Fact]
    public void Taken_email_is_checked_using_normalized_value()
    {
        var d = SeedAdminPlan.Decide(false, Valid(), email => email == "admin@example.invalid");
        Assert.Equal(SeedAdminAction.EmailTaken, d.Action);
    }

    [Theory]
    [InlineData("Admin", true)]
    [InlineData("HR", true)]
    [InlineData("Manager", false)]
    [InlineData("admin", false)]
    [InlineData(null, false)]
    public void Only_two_roles_are_valid(string? role, bool expected) => Assert.Equal(expected, UserRoles.IsValid(role));
}
