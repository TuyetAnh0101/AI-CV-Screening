using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;

namespace SmartCv.Tests.Auth;

public class A30RequestTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"email\":\"a@example.invalid\",\"Email\":\"b@example.invalid\"}")]
    [InlineData("{\"fullName\":42}")]
    public async Task Malformed_account_JSON_is_safe_400_without_any_write(string body)
    {
        using var factory = A28TestSupport.Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password()); using var http = A30AccountEndpointTests.Client(factory, admin);
        using var response = await http.PostAsync("/api/users", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await A28TestSupport.WithDb(factory, async db => Assert.Equal(1, await db.Users.CountAsync()));
    }

    [Theory]
    [InlineData("text/plain", 415)]
    [InlineData("oversized", 413)]
    [InlineData("charset", 415)]
    public async Task Account_body_limits_and_content_type_errors_do_not_become_500(string kind, int status)
    {
        using var factory = A28TestSupport.Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password()); using var http = A30AccountEndpointTests.Client(factory, admin);
        using var content = new StringContent(kind == "oversized" ? new string('x', 8193) : "{}", Encoding.UTF8, kind == "text/plain" ? kind : "application/json");
        if (kind == "charset") content.Headers.ContentType!.CharSet = "unsupported-synthetic";
        using var response = await http.PostAsync("/api/auth/change-password", content);
        Assert.Equal(status, (int)response.StatusCode); Assert.DoesNotContain("StackTrace", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=201")]
    [InlineData("page=2147483647&pageSize=200")]
    [InlineData("role=Manager")]
    [InlineData("isActive=invalid")]
    [InlineData("role=HR&role=Admin")]
    public async Task Invalid_list_filters_are_field_errors(string query)
    {
        using var factory = A28TestSupport.Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password()); using var http = A30AccountEndpointTests.Client(factory, admin);
        using var response = await http.GetAsync("/api/users?" + query); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await A29AuthorizationTests.Json(response)).GetProperty("errors").EnumerateObject().Any());
    }

    [Fact]
    public async Task List_is_read_only_bounded_filtered_and_stably_sorted_for_the_existing_B29_array_contract()
    {
        using var factory = A28TestSupport.Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        var a = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR"); var b = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        var inactive = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR", active: false);
        await A28TestSupport.WithDb(factory, async db => { (await db.Users.FindAsync(a.UserId))!.FullName = "A"; (await db.Users.FindAsync(b.UserId))!.FullName = "B"; await db.SaveChangesAsync(); });
        using var http = A30AccountEndpointTests.Client(factory, admin);
        using var page = await http.GetAsync("/api/users?role=HR&isActive=true&pageSize=1&page=2"); Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var rows = (await page.Content.ReadFromJsonAsync<List<UserDto>>())!; Assert.Equal(b.UserId, Assert.Single(rows).UserId);
        Assert.Equal("2", page.Headers.GetValues("X-Total-Count").Single()); Assert.Equal("1", page.Headers.GetValues("X-Page-Size").Single());
        using var search = await http.GetAsync("/api/users?search=" + Uri.EscapeDataString(inactive.Email));
        Assert.Equal(inactive.UserId, Assert.Single((await search.Content.ReadFromJsonAsync<List<UserDto>>())!).UserId);
        await A28TestSupport.WithDb(factory, async db => Assert.Equal(4, await db.Users.CountAsync()));
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("HR", 403)]
    [InlineData("temporary-Admin", 403)]
    public async Task Account_management_cannot_be_entered_without_the_required_current_privileges(string who, int status)
    {
        using var factory = A28TestSupport.Factory(); var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), who == "HR" ? "HR" : "Admin", mustChange: who == "temporary-Admin");
        using var http = who == "anonymous" ? factory.CreateClient() : A30AccountEndpointTests.Client(factory, user);
        Assert.Equal(status, (int)(await http.GetAsync("/api/users")).StatusCode);
        Assert.Equal(status, (int)(await A30AccountEndpointTests.Create(http)).StatusCode);
        Assert.Equal(status, (int)(await http.PostAsync($"/api/users/{user.UserId}/reset-password", null)).StatusCode);
        await A28TestSupport.WithDb(factory, async db => Assert.Equal(1, await db.Users.CountAsync()));
    }

    [Fact]
    public async Task Account_mutations_keep_existing_recruitment_history_unchanged()
    {
        using var factory = A28TestSupport.Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password()); var hr = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        var id = (await Applications.ApplicationStageEndpointTests.SeedAsync(factory.Services, 1))[0];
        await A28TestSupport.WithDb(factory, async db => { db.ApplicationEvents.Add(new ApplicationEvent { UserId = hr.UserId, ApplicationId = id, FromStage = "New", ToStage = "Reviewing", Actor = "Original actor", Note = "Original history" }); await db.SaveChangesAsync(); });
        using var http = A30AccountEndpointTests.Client(factory, admin);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"/api/users/{hr.UserId}/lock", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"/api/users/{hr.UserId}/reset-password", null)).StatusCode);
        await A28TestSupport.WithDb(factory, async db =>
        {
            var history = await db.ApplicationEvents.SingleAsync(); Assert.Equal(hr.UserId, history.UserId); Assert.Equal("Original actor", history.Actor); Assert.Equal("Original history", history.Note);
            Assert.Equal(2, await db.Users.CountAsync());
        });
    }

    [Fact]
    public async Task Seeder_restarts_with_different_or_invalid_settings_never_overwrite_an_existing_Admin()
    {
        using var db = TestSupport.InMemoryDb(Guid.NewGuid().ToString("N"));
        var admin = new User { UserId = 1, Email = "synthetic-admin@example.invalid", FullName = "Synthetic existing Admin", Role = "Admin", IsActive = true, CreatedAt = DateTime.UtcNow };
        admin.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<User>().HashPassword(admin, A28TestSupport.Password());
        db.Users.Add(admin); await db.SaveChangesAsync(); var before = A30AccountEndpointTests.Fingerprint(admin);
        for (var i = 0; i < 3; i++) await SmartCv.Api.Auth.AdminSeeder.SeedAdminAsync(db, new Microsoft.AspNetCore.Identity.PasswordHasher<User>(),
            new SmartCv.Core.Options.SeedAdminOptions { Email = "different@example.invalid", FullName = "Different", Password = "invalid" }, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.True(before == A30AccountEndpointTests.Fingerprint(await db.Users.SingleAsync())); Assert.Equal(1, await db.Users.CountAsync());
    }
}