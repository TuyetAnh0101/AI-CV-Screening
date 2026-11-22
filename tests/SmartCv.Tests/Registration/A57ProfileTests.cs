using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Contracts;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.Registration;

public sealed class A57ProfileTests
{
    [Fact]
    public async Task Profile_IsPrivateAndIncludesNoPasswordHash()
    {
        using var factory = A28TestSupport.Factory(new() { ["Email:WorkerEnabled"] = "false" });
        using var anonymous = factory.CreateClient();
        using var unauthorized = await anonymous.GetAsync("/api/auth/me/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), role: "HR");
        using var client = A29AuthorizationTests.Client(factory, user);
        using var response = await client.GetAsync("/api/auth/me/profile");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<ProfileDto>();
        Assert.NotNull(profile);
        Assert.Equal(user.UserId, profile.UserId);
        Assert.Equal(user.Email, profile.Email);
        Assert.True(profile.HasPassword);
        Assert.True(profile.EmailVerified);
        var serialized = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProfileUpdate_ChangesOnlyTheCurrentUserAndMeReturnsNewName()
    {
        using var factory = A28TestSupport.Factory(new() { ["Email:WorkerEnabled"] = "false" });
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), role: "HR");
        var other = await A28TestSupport.Seed(factory, A28TestSupport.Password(), role: "HR");
        using var client = A29AuthorizationTests.Client(factory, user);
        using var response = await client.PutAsJsonAsync("/api/auth/me/profile", new UpdateMyProfileRequest { FullName = "  Tên   cá nhân  " });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Tên cá nhân", (await response.Content.ReadFromJsonAsync<ProfileDto>())!.FullName);
        using var me = await client.GetAsync("/api/auth/me");
        Assert.Equal("Tên cá nhân", (await me.Content.ReadFromJsonAsync<MeDto>())!.FullName);
        await A28TestSupport.WithDb(factory, async db =>
        {
            Assert.Equal("Tên cá nhân", (await db.Users.AsNoTracking().SingleAsync(x => x.UserId == user.UserId)).FullName);
            Assert.Equal(other.FullName, (await db.Users.AsNoTracking().SingleAsync(x => x.UserId == other.UserId)).FullName);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyProfileName_IsRejected(string name)
    {
        using var factory = A28TestSupport.Factory(new() { ["Email:WorkerEnabled"] = "false" });
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), role: "HR");
        using var client = A29AuthorizationTests.Client(factory, user);
        using var response = await client.PutAsJsonAsync("/api/auth/me/profile", new { fullName = name });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TooLongProfileName_IsRejected()
    {
        using var factory = A28TestSupport.Factory(new() { ["Email:WorkerEnabled"] = "false" });
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), role: "HR");
        using var client = A29AuthorizationTests.Client(factory, user);
        using var response = await client.PutAsJsonAsync("/api/auth/me/profile", new { fullName = new string('A', 151) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ProfileUpdate_StaleIfMatchReturnsConflict()
    {
        using var factory = A28TestSupport.Factory(new() { ["Email:WorkerEnabled"] = "false" });
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), role: "HR");
        using var client = A29AuthorizationTests.Client(factory, user);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/auth/me/profile")
        { Content = JsonContent.Create(new UpdateMyProfileRequest { FullName = "Đổi tên" }) };
        request.Headers.TryAddWithoutValidation("If-Match", "\"" + new string('A', 64) + "\"");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
