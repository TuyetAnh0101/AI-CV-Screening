using SmartCv.Core.Auth;
using SmartCv.Core.Constants;
using SmartCv.Core.Options;

namespace SmartCv.Tests.Registration;

public sealed class A56LoginRulesTests
{
    [Fact]
    public void MissingWorkspace_CanOnlyBeProvisionedForVerifiedNonAdminSources()
    {
        var verified = new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(RegistrationRules.CanProvisionOnLogin(SignupSources.Local, verified, true));
        Assert.True(RegistrationRules.CanProvisionOnLogin(SignupSources.Google, verified, true));
        Assert.False(RegistrationRules.CanProvisionOnLogin(SignupSources.Admin, verified, true));
        Assert.False(RegistrationRules.CanProvisionOnLogin(SignupSources.Local, null, true));
        Assert.False(RegistrationRules.CanProvisionOnLogin(SignupSources.Local, verified, false));
    }

    [Fact]
    public void UnverifiedLocalIsTheOnlyResendEligibleAccount()
    {
        var now = DateTime.UtcNow;
        Assert.True(RegistrationRules.NeedsVerification(true, null, SignupSources.Local));
        Assert.False(RegistrationRules.NeedsVerification(true, now, SignupSources.Local));
        Assert.False(RegistrationRules.NeedsVerification(true, null, SignupSources.Google));
        Assert.False(RegistrationRules.NeedsVerification(false, null, SignupSources.Local));
    }

    [Fact]
    public void RegistrationIsDisabledByDefault()
    {
        var flags = new RegistrationOptions();
        Assert.False(flags.Enabled);
        Assert.True(flags.IsValid());
    }
}
