using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Api.Auth;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.E2E;

/// <summary>
/// B57 E11 with a FAKE Google principal (claims as produced by the OIDC handler after signature/nonce/PKCE
/// validation) driving the real ExternalLoginService + DB + JWT. It does NOT exercise accounts.google.com:
/// real Google, state/nonce/PKCE validation by the OpenIdConnect handler remain NOT VERIFIED without test credentials.
/// </summary>
public sealed class B57GoogleLoginTests
{
    private static ClaimsPrincipal Google(string sub, string? email, bool verified = true) =>
        new(new ClaimsIdentity(new[] { new Claim("sub", sub), new Claim("email_verified", verified ? "true" : "false"), new Claim("name", "Người dùng Google B57") }
            .Concat(email is null ? [] : new[] { new Claim("email", email) }), "google"));

    private static ExternalLoginService Service(IServiceProvider services) => ActivatorUtilities.CreateInstance<ExternalLoginService>(services,
        (IConfiguration)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Authentication:Google:Enabled"] = "true", ["App:WebBaseUrl"] = "https://localhost:7001" }).Build());

    /// <summary>The principal the JWT bearer handler would build for this user's token.</summary>
    private static ClaimsPrincipal Session(User user, int? workspace) =>
        new(new ClaimsIdentity(new JwtSecurityTokenHandler().ReadJwtToken(A28TestSupport.Token(user, workspaceId: workspace)).Claims, "Bearer", "name", "role"));

    private static string Code(ExternalCompletion completion) =>
        Uri.UnescapeDataString(completion.RedirectUrl.Split("#code=")[1].Split('&')[0]);

    private static string Ticket(string start) => Uri.UnescapeDataString(start.Split("ticket=")[1]);

    private static async Task<T> InScope<T>(B57World world, Func<ExternalLoginService, Task<T>> action)
    {
        using var scope = world.Factory.Services.CreateScope();
        return await action(Service(scope.ServiceProvider));
    }

    /// <summary>AccountFailure is internal to the API; read its public contract (status + machine code) by reflection.</summary>
    private static async Task<(int Status, string Code)> Failure(Func<Task> action)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(action);
        Assert.Equal("AccountFailure", error.GetType().Name);
        var type = error.GetType(); const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        return ((int)type.GetProperty("Status", flags)!.GetValue(error)!, (string)type.GetProperty("Code", flags)!.GetValue(error)!);
    }

    private static Task<B57World> World() => B57World.CreateAsync(new() { ["Registration:Enabled"] = "true", ["Authentication:Google:Enabled"] = "false" });

    [Fact]
    public async Task E11_new_Google_account_gets_its_own_empty_personal_workspace_and_relogin_is_the_same_user()
    {
        using var world = await World();
        var email = "b57-google-" + Guid.NewGuid().ToString("N")[..10] + "@example.invalid";
        var first = await InScope(world, s => s.CompleteAsync(Google("b57-sub-new", email), "google", "login", null, "/reports", default));
        Assert.Null(first.FailureCode);
        Assert.StartsWith("https://localhost:7001/auth/callback#code=", first.RedirectUrl);
        Assert.DoesNotContain(email, first.RedirectUrl); Assert.DoesNotContain("b57-sub-new", first.RedirectUrl);
        var code = Code(first);
        var login = await InScope(world, s => s.ExchangeAsync(code, default));
        Assert.Equal("HR", login.Login.User.Role);
        Assert.Equal(("Personal", "Owner"), (login.Login.User.Workspace!.Kind, login.Login.User.Workspace.Role));
        Assert.DoesNotContain(B57World.Workspaces, w => w == login.Login.User.Workspace.Id);
        Assert.Equal("/reports", login.ReturnUrl);
        var reuse = await Failure(() => InScope(world, s => s.ExchangeAsync(code, default)));
        Assert.Equal((400, "token_invalid"), reuse);

        using var http = world.Anonymous();
        http.DefaultRequestHeaders.Authorization = new("Bearer", login.Login.Token);
        var summary = (await http.GetFromJsonAsync<HrReportSummaryDto>("/api/hr/reports/summary"))!;
        Assert.Equal(0, summary.Applications);
        foreach (var path in new[] { "/api/jobs", "/api/candidates", "/api/runs", "/api/hr/reports/rows" })
            Assert.DoesNotContain("B57_", await http.GetStringAsync(path));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/runs/{world.Data[2].Run}/ranking/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/admin/reports/v2/preview")).StatusCode);

        var again = await InScope(world, s => s.CompleteAsync(Google("b57-sub-new", email), "google", "login", null, "/jobs", default));
        var second = await InScope(world, s => s.ExchangeAsync(Code(again), default));
        Assert.Equal(login.Login.User.UserId, second.Login.User.UserId);
        Assert.Equal(login.Login.User.Workspace.Id, second.Login.User.Workspace!.Id);
    }

    [Fact]
    public async Task E11_link_with_ticket_login_reaches_only_the_owner_workspace_ticket_is_single_use_and_cannot_steal_a_linked_subject()
    {
        using var world = await World();
        var start = await InScope(world, s => s.CreateLinkStartUrlAsync(Session(world.OwnerA, B57World.OwnerAWorkspace), "google", default));
        var ticket = Ticket(start);
        var linked = await InScope(world, s => s.CompleteAsync(Google("b57-sub-a", "different-address@example.invalid"), "google", "link", ticket, null, default));
        Assert.Null(linked.FailureCode);
        Assert.Equal("https://localhost:7001/account?linked=google", linked.RedirectUrl);
        var replay = await InScope(world, s => s.CompleteAsync(Google("b57-sub-other", null), "google", "link", ticket, null, default));
        Assert.Equal("token_invalid", replay.FailureCode);

        var login = await InScope(world, s => s.CompleteAsync(Google("b57-sub-a", "different-address@example.invalid"), "google", "login", null, null, default));
        var session = await InScope(world, s => s.ExchangeAsync(Code(login), default));
        Assert.Equal(world.OwnerA.UserId, session.Login.User.UserId);
        Assert.Equal(B57World.OwnerAWorkspace, session.Login.User.Workspace!.Id);
        using var http = world.Anonymous();
        http.DefaultRequestHeaders.Authorization = new("Bearer", session.Login.Token);
        var rows = await http.GetStringAsync("/api/hr/reports/rows?anonymous=false");
        Assert.Contains("B57_OWNER_A_PRIVATE", rows); Assert.DoesNotContain("B57_OWNER_B_PRIVATE", rows);

        // Owner B tries to attach the subject already linked to Owner A.
        var bTicket = Ticket(await InScope(world, s => s.CreateLinkStartUrlAsync(Session(world.OwnerB, B57World.OwnerBWorkspace), "google", default)));
        var steal = await InScope(world, s => s.CompleteAsync(Google("b57-sub-a", world.OwnerB.Email), "google", "link", bTicket, null, default));
        Assert.Equal("already_linked", steal.FailureCode);
        Assert.StartsWith("https://localhost:7001/account?external_error=", steal.RedirectUrl);
        await world.WithGlobalDb(async db =>
        {
            Assert.Equal(world.OwnerA.UserId, (await db.UserExternalLogins.SingleAsync(x => x.ProviderKey == "b57-sub-a")).UserId);
            Assert.False(await db.UserExternalLogins.AnyAsync(x => x.UserId == world.OwnerB.UserId));
            var rejected = await db.AuditEvents.Where(e => e.ActionCode == "auth.external_rejected").ToListAsync();
            Assert.Contains(rejected, e => e.FailureCode == "already_linked");
            Assert.All(rejected, e => { Assert.DoesNotContain("b57-sub", e.SafeSummary ?? ""); Assert.DoesNotContain("@", e.SafeSummary ?? ""); });
        });
    }

    [Fact]
    public async Task E11_same_email_locked_unverified_admin_and_invalid_callback_inputs_are_rejected_without_side_effects()
    {
        using var world = await World();
        // Duplicate e-mail never auto-links (even a verified Google address of an existing Owner).
        var duplicate = await InScope(world, s => s.CompleteAsync(Google("b57-sub-dup", world.OwnerB.Email), "google", "login", null, null, default));
        Assert.Equal("link_required", duplicate.FailureCode);
        Assert.DoesNotContain("#code=", duplicate.RedirectUrl);
        // Unverified or missing e-mail.
        Assert.Equal("email_unverified", (await InScope(world, s => s.CompleteAsync(Google("b57-sub-unv", "b57-unverified@example.invalid", verified: false), "google", "login", null, null, default))).FailureCode);
        Assert.Equal("email_unverified", (await InScope(world, s => s.CompleteAsync(Google("b57-sub-none", null), "google", "login", null, null, default))).FailureCode);
        // Locked account with an existing link.
        await world.WithGlobalDb(async db =>
        {
            db.UserExternalLogins.Add(new UserExternalLogin { UserId = world.Locked.UserId, Provider = "google", ProviderKey = "b57-sub-locked", LinkedAt = DateTime.UtcNow });
            db.UserExternalLogins.Add(new UserExternalLogin { UserId = world.Admin.UserId, Provider = "google", ProviderKey = "b57-sub-admin", LinkedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });
        Assert.Equal("account_locked", (await InScope(world, s => s.CompleteAsync(Google("b57-sub-locked", world.Locked.Email), "google", "login", null, null, default))).FailureCode);
        Assert.Equal("admin_not_allowed", (await InScope(world, s => s.CompleteAsync(Google("b57-sub-admin", world.Admin.Email), "google", "login", null, null, default))).FailureCode);
        var adminLink = await Failure(() => InScope(world, s => s.CreateLinkStartUrlAsync(Session(world.Admin, B57World.Internal), "google", default)));
        Assert.Equal((403, "admin_not_allowed"), adminLink);
        // Callback-level failures: bad intent, missing/forged ticket, empty/oversized subject.
        Assert.Equal("provider_error", (await InScope(world, s => s.CompleteAsync(Google("b57-sub-x", "x@example.invalid"), "google", "steal", null, null, default))).FailureCode);
        Assert.Equal("token_invalid", (await InScope(world, s => s.CompleteAsync(Google("b57-sub-x", "x@example.invalid"), "google", "link", null, null, default))).FailureCode);
        Assert.Equal("token_invalid", (await InScope(world, s => s.CompleteAsync(Google("b57-sub-x", "x@example.invalid"), "google", "link", new string('A', 43), null, default))).FailureCode);
        Assert.Equal("provider_error", (await InScope(world, s => s.CompleteAsync(Google("", "x@example.invalid"), "google", "login", null, null, default))).FailureCode);
        Assert.Equal("provider_error", (await InScope(world, s => s.CompleteAsync(Google(new string('s', 201), "x@example.invalid"), "google", "login", null, null, default))).FailureCode);
        Assert.Equal("provider_error", (await InScope(world, s => s.CompleteAsync(Google("b57-sub-x", "x@example.invalid"), "linkedin", "login", null, null, default))).FailureCode);
        var unsafeReturn = await InScope(world, s => s.CompleteAsync(Google("b57-sub-ret", "b57-ret@example.invalid"), "google", "login", null, "https://evil.example/steal", default));
        Assert.Equal("/dashboard", (await InScope(world, s => s.ExchangeAsync(Code(unsafeReturn), default))).ReturnUrl);
        await world.WithGlobalDb(async db =>
        {
            Assert.Equal(1, await db.Users.CountAsync(u => u.Email == world.OwnerB.Email));
            Assert.False(await db.Users.AnyAsync(u => u.Email == "b57-unverified@example.invalid" || u.Email == "x@example.invalid"));
            Assert.False(await db.UserExternalLogins.AnyAsync(x => x.ProviderKey == "b57-sub-dup"));
        });
    }

    [Fact]
    public async Task E11_unlink_keeps_last_login_method_and_unlinked_subject_no_longer_opens_the_workspace()
    {
        using var world = await World();
        var email = "b57-only-google-" + Guid.NewGuid().ToString("N")[..8] + "@example.invalid";
        var created = await InScope(world, s => s.CompleteAsync(Google("b57-sub-only", email), "google", "login", null, null, default));
        var onlyGoogle = await InScope(world, s => s.ExchangeAsync(Code(created), default));
        User user = null!;
        await world.WithGlobalDb(async db => user = await db.Users.AsNoTracking().SingleAsync(u => u.UserId == onlyGoogle.Login.User.UserId));
        var last = await Failure(() => InScope(world, async s => { await s.UnlinkAsync(Session(user, onlyGoogle.Login.User.Workspace!.Id), "google", default); return 0; }));
        Assert.Equal(409, last.Status);
        await world.WithGlobalDb(async db => Assert.True(await db.UserExternalLogins.AnyAsync(x => x.UserId == user.UserId)));

        // Owner A has a password, so unlinking Google is allowed; the subject then cannot reach Owner A any more.
        var ticket = Ticket(await InScope(world, s => s.CreateLinkStartUrlAsync(Session(world.OwnerA, B57World.OwnerAWorkspace), "google", default)));
        Assert.Null((await InScope(world, s => s.CompleteAsync(Google("b57-sub-a2", null), "google", "link", ticket, null, default))).FailureCode);
        await InScope(world, async s => { await s.UnlinkAsync(Session(world.OwnerA, B57World.OwnerAWorkspace), "google", default); return 0; });
        var after = await InScope(world, s => s.CompleteAsync(Google("b57-sub-a2", world.OwnerA.Email), "google", "login", null, null, default));
        Assert.Equal("link_required", after.FailureCode);
        await world.WithGlobalDb(async db => Assert.False(await db.UserExternalLogins.AnyAsync(x => x.UserId == world.OwnerA.UserId)));
    }
}
