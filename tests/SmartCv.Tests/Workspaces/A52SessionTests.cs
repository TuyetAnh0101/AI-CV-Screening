using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmartCv.Api.Auth;
using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Workspaces;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Workspaces;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests;

/// <summary>A52: JWT claim smartcv_ws, kiểm tra phiên với database, tương thích token cũ, phân quyền và cách ly song song (API thật, InMemory).</summary>
public class A52SessionTests
{
    private static HttpClient Bearer(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static async Task SecondWorkspaceAsync(WebApplicationFactory<Program> factory, bool withJob = true) =>
        await A28TestSupport.WithDb(factory, async db =>
        {
            await db.EnsureInternalWorkspaceAsync();
            db.Workspaces.Add(new Workspace { WorkspaceId = 2, Name = "Không gian cá nhân", Kind = WorkspaceKinds.Personal, IsActive = true, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            foreach (var (workspace, title) in new[] { (1, "A52 job 1"), (2, "A52 job 2") })
            {
                if (!withJob) break;
                using var scope = WorkspaceScope.Begin(workspace);
                db.Jobs.Add(new Job { Title = title, MinYearsExperience = 0, CreatedAt = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
        });

    private static async Task<string> JobsBodyAsync(HttpClient http)
    {
        using var response = await http.GetAsync("/api/jobs?activeOnly=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;

    // ---------- Login và /me ----------

    [Fact]
    public async Task Login_issues_the_signed_workspace_claim_and_me_reports_the_workspace()
    {
        using var factory = A28TestSupport.Factory(); var password = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(factory, password, "HR");
        using var anonymous = factory.CreateClient();
        using var login = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = user.Email, Password = password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var result = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        var jwt = new JsonWebToken(result.Token);
        Assert.Equal("1", Assert.Single(jwt.Claims, c => c.Type == SmartCvClaims.Workspace).Value);
        // Vai trò workspace KHÔNG nằm trong token (nguồn là database); Role hệ thống vẫn là HR.
        Assert.DoesNotContain(jwt.Claims, c => c.Value is "Owner" or "Member");
        Assert.Equal("HR", jwt.GetClaim("role").Value);
        Assert.Equal(("Internal", "Member", 1), (result.User.Workspace!.Kind, result.User.Workspace.Role, result.User.Workspace.Id));

        using var http = Bearer(factory, result.Token);
        var me = (await http.GetFromJsonAsync<MeDto>("/api/auth/me"))!;
        Assert.Equal(("SCApp nội bộ", "Internal", "Member"), (me.Workspace!.Name, me.Workspace.Kind, me.Workspace.Role));
        Assert.Equal("HR", me.Role);
    }

    [Fact]
    public async Task Login_prefers_the_internal_workspace_and_skips_inactive_ones()
    {
        using var factory = A28TestSupport.Factory(); var password = A28TestSupport.Password();
        await SecondWorkspaceAsync(factory, withJob: false);
        var user = await A28TestSupport.Seed(factory, password, "HR", memberOfWorkspace: 2, workspaceRole: "Owner");
        await A28TestSupport.WithDb(factory, async db => { db.Add(new WorkspaceMember { WorkspaceId = 1, UserId = user.UserId, Role = "Member" }); await db.SaveChangesAsync(); });
        using var anonymous = factory.CreateClient();
        async Task<string?> ClaimAsync()
        {
            using var login = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = user.Email, Password = password });
            return new JsonWebToken((await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token).Claims.SingleOrDefault(c => c.Type == SmartCvClaims.Workspace)?.Value;
        }
        Assert.Equal("1", await ClaimAsync());   // nhiều workspace hợp lệ: ưu tiên Internal
        await A28TestSupport.WithDb(factory, async db => { (await db.Workspaces.SingleAsync(w => w.WorkspaceId == 1)).IsActive = false; await db.SaveChangesAsync(); });
        Assert.Equal("2", await ClaimAsync());   // workspace 1 bị khóa: chọn workspace còn hiệu lực
        await A28TestSupport.WithDb(factory, async db => { (await db.Workspaces.SingleAsync(w => w.WorkspaceId == 2)).IsActive = false; await db.SaveChangesAsync(); });
        Assert.Null(await ClaimAsync());         // không còn workspace hợp lệ: token không có claim, không tự gán gì
    }

    [Fact]
    public async Task Account_without_membership_logs_in_without_a_workspace_and_is_never_auto_assigned()
    {
        using var factory = A28TestSupport.Factory(); var password = A28TestSupport.Password();
        await A28TestSupport.WithDb(factory, db => db.EnsureInternalWorkspaceAsync());
        var user = await A28TestSupport.Seed(factory, password, "HR", memberOfWorkspace: null);
        using var anonymous = factory.CreateClient();
        using var login = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = user.Email, Password = password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var result = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        Assert.DoesNotContain(new JsonWebToken(result.Token).Claims, c => c.Type == SmartCvClaims.Workspace);
        Assert.Null(result.User.Workspace);
        using var http = Bearer(factory, result.Token);
        Assert.Null((await http.GetFromJsonAsync<MeDto>("/api/auth/me"))!.Workspace);
        using var business = await http.GetAsync("/api/jobs");
        Assert.Equal(HttpStatusCode.Unauthorized, business.StatusCode);
        await A28TestSupport.WithDb(factory, async db => Assert.Equal(0, await db.WorkspaceMembers.CountAsync(m => m.UserId == user.UserId)));
    }

    // ---------- Phiên: thu hồi membership / khóa workspace / token cũ / token giả ----------

    [Fact]
    public async Task Revoked_membership_or_inactive_workspace_rejects_the_very_next_request()
    {
        using var factory = A28TestSupport.Factory();
        await SecondWorkspaceAsync(factory);
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        using var http = Bearer(factory, A28TestSupport.Token(user));
        Assert.Contains("A52 job 1", await JobsBodyAsync(http));

        await A28TestSupport.WithDb(factory, async db => { (await db.Workspaces.SingleAsync(w => w.WorkspaceId == 1)).IsActive = false; await db.SaveChangesAsync(); });
        using (var response = await http.GetAsync("/api/jobs")) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using (var response = await http.GetAsync("/api/auth/me")) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);   // claim có nhưng không còn hợp lệ => phiên hết hiệu lực

        await A28TestSupport.WithDb(factory, async db => { (await db.Workspaces.SingleAsync(w => w.WorkspaceId == 1)).IsActive = true; await db.SaveChangesAsync(); });
        Assert.Contains("A52 job 1", await JobsBodyAsync(http));   // bật lại workspace: cùng token dùng được (không đổi gì trong token)

        await A28TestSupport.WithDb(factory, async db => { db.WorkspaceMembers.RemoveRange(db.WorkspaceMembers.Where(m => m.UserId == user.UserId)); await db.SaveChangesAsync(); });
        using (var response = await http.GetAsync("/api/jobs")) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // Không tự cấp lại membership và không đổi Role.
        await A28TestSupport.WithDb(factory, async db =>
        {
            Assert.Equal(0, await db.WorkspaceMembers.CountAsync(m => m.UserId == user.UserId));
            Assert.Equal("HR", (await db.Users.SingleAsync(u => u.UserId == user.UserId)).Role);
        });
    }

    [Fact]
    public async Task Token_without_the_claim_is_rejected_by_business_routes_with_401_but_me_still_works()
    {
        using var factory = A28TestSupport.Factory();
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        using var http = Bearer(factory, A28TestSupport.Token(user, workspaceId: null));   // token cấp trước A52
        using var business = await http.GetAsync("/api/jobs");
        Assert.Equal(HttpStatusCode.Unauthorized, business.StatusCode);   // không phải 403
        Assert.Equal("authentication_required", await CodeAsync(business));
        Assert.Contains("Bearer", business.Headers.WwwAuthenticate.Select(h => h.Scheme));
        using var me = await http.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = (await me.Content.ReadFromJsonAsync<MeDto>())!;
        Assert.Null(body.Workspace);
        Assert.Equal(user.Email, body.Email);
    }

    [Fact]
    public async Task Old_token_can_change_password_and_receives_a_token_with_the_workspace()
    {
        using var factory = A28TestSupport.Factory(); var password = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(factory, password, "HR");
        using var http = Bearer(factory, A28TestSupport.Token(user, workspaceId: null));
        using var response = await http.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest { CurrentPassword = password, NewPassword = A28TestSupport.Password() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<ChangePasswordResponse>())!;
        Assert.Equal("1", new JsonWebToken(result.Token).GetClaim(SmartCvClaims.Workspace).Value);
        Assert.Equal(1, result.User.Workspace!.Id);
        using var upgraded = Bearer(factory, result.Token);
        Assert.NotNull(await JobsBodyAsync(upgraded));
    }

    [Theory]
    [InlineData("not-a-member")]   // claim trỏ workspace có thật nhưng tài khoản không phải thành viên
    [InlineData("missing")]        // workspace không tồn tại
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("text")]
    [InlineData("duplicate")]
    public async Task Forged_or_invalid_workspace_claims_are_rejected_everywhere(string kind)
    {
        using var factory = A28TestSupport.Factory();
        await SecondWorkspaceAsync(factory, withJob: false);
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        var options = A28TestSupport.Settings();
        var claims = new List<Claim>
        {
            new(SmartCvClaims.UserId, user.UserId.ToString()), new(SmartCvClaims.Email, user.Email), new(SmartCvClaims.Name, user.FullName),
            new(SmartCvClaims.Role, user.Role), new(SmartCvClaims.Stamp, user.SecurityStamp), new(SmartCvClaims.MustChangePassword, "false")
        };
        switch (kind)
        {
            case "not-a-member": claims.Add(new(SmartCvClaims.Workspace, "2")); break;
            case "missing": claims.Add(new(SmartCvClaims.Workspace, "999")); break;
            case "zero": claims.Add(new(SmartCvClaims.Workspace, "0")); break;
            case "negative": claims.Add(new(SmartCvClaims.Workspace, "-1")); break;
            case "text": claims.Add(new(SmartCvClaims.Workspace, "internal")); break;
            default: claims.Add(new(SmartCvClaims.Workspace, "1")); claims.Add(new(SmartCvClaims.Workspace, "1")); break;
        }
        var now = DateTime.UtcNow;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer, Audience = options.Audience, IssuedAt = now, NotBefore = now, Expires = now.AddHours(1), Subject = new ClaimsIdentity(claims),
            SigningCredentials = new(JwtTokenService.SigningKey(options), SecurityAlgorithms.HmacSha256)
        });
        using var http = Bearer(factory, token);
        foreach (var path in new[] { "/api/jobs", "/api/auth/me", "/api/users" })
        {
            using var response = await http.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Workspace_claim_does_not_bypass_must_change_password_or_security_stamp()
    {
        using var factory = A28TestSupport.Factory();
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR", mustChange: true);
        using var http = Bearer(factory, A28TestSupport.Token(user));
        using var business = await http.GetAsync("/api/jobs");
        Assert.Equal(HttpStatusCode.Forbidden, business.StatusCode);
        Assert.Equal("password_change_required", await CodeAsync(business));
        using var me = await http.GetAsync("/api/auth/me");   // SignedIn vẫn dùng được
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        var stale = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        var token = A28TestSupport.Token(stale);
        await A28TestSupport.WithDb(factory, async db => { (await db.Users.SingleAsync(u => u.UserId == stale.UserId)).SecurityStamp = User.NewStamp(); await db.SaveChangesAsync(); });
        using var revoked = await Bearer(factory, token).GetAsync("/api/jobs");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    // ---------- Phân quyền và cách ly ----------

    [Theory]
    [InlineData("HR", HttpStatusCode.Forbidden)]
    [InlineData("Admin", HttpStatusCode.OK)]
    public async Task System_role_still_decides_admin_endpoints_and_workspace_role_does_not_grant_them(string role, HttpStatusCode expected)
    {
        using var factory = A28TestSupport.Factory();
        // Cả HR lẫn Admin đều là thành viên workspace nội bộ; HR là Owner nhưng vẫn không có quyền Admin.
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), role, workspaceRole: role == "HR" ? "Owner" : "Member");
        using var http = Bearer(factory, A28TestSupport.Token(user));
        using var response = await http.GetAsync("/api/users");
        Assert.Equal(expected, response.StatusCode);
        using var business = await http.GetAsync("/api/jobs");
        // Since #114 the system role also bounds business work: System Admin is not a business operator.
        Assert.Equal(role == "HR" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, business.StatusCode);
    }

    [Theory]
    [InlineData("HR")]
    [InlineData("Admin")]
    public async Task Business_data_is_limited_to_the_workspace_of_the_session_even_for_admin(string role)
    {
        using var factory = A28TestSupport.Factory();
        await SecondWorkspaceAsync(factory);
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), role);
        using var http = Bearer(factory, A28TestSupport.Token(user));
        int foreignId = 0;
        await A28TestSupport.WithDb(factory, async db => { using var system = WorkspaceScope.BeginSystem("test: lấy ID workspace khác"); foreignId = (await db.Jobs.SingleAsync(j => j.Title == "A52 job 2")).JobId; });
        if (role == "Admin")
        {
            // Since #114 (B57 R1): Admin gets 403 for own and foreign business data alike, never the records.
            foreach (var path in new[] { "/api/jobs?activeOnly=false", $"/api/jobs/{foreignId}" })
            {
                using var denied = await http.GetAsync(path);
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                Assert.DoesNotContain("A52 job", await denied.Content.ReadAsStringAsync());
            }
            return;
        }
        var body = await JobsBodyAsync(http);
        Assert.Contains("A52 job 1", body);
        Assert.DoesNotContain("A52 job 2", body);
        // ID của workspace khác không dùng được (cách ly đầy đủ từng endpoint là A53).
        using var other = await http.GetAsync($"/api/jobs/{foreignId}");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task Two_users_in_two_workspaces_in_parallel_never_see_each_others_data()
    {
        using var factory = A28TestSupport.Factory();
        await SecondWorkspaceAsync(factory);
        var first = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR", memberOfWorkspace: 1);
        var second = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR", memberOfWorkspace: 2, workspaceRole: "Owner");
        // A Personal Owner is verified by Workspaces.OwnerUserId (RoleBoundaryRules), not by the membership row alone.
        await A28TestSupport.WithDb(factory, async db => { (await db.Workspaces.SingleAsync(w => w.WorkspaceId == 2)).OwnerUserId = second.UserId; await db.SaveChangesAsync(); });
        using var a = Bearer(factory, A28TestSupport.Token(first, workspaceId: 1));
        using var b = Bearer(factory, A28TestSupport.Token(second, workspaceId: 2));
        var results = await Task.WhenAll(Enumerable.Range(0, 60).Select(i => Task.Run(async () =>
        {
            var one = i % 2 == 0;
            var body = await JobsBodyAsync(one ? a : b);
            return one ? body.Contains("A52 job 1") && !body.Contains("A52 job 2") : body.Contains("A52 job 2") && !body.Contains("A52 job 1");
        })));
        Assert.All(results, Assert.True);
        Assert.False(WorkspaceScope.IsActive);
    }

    [Fact]
    public async Task Login_audit_carries_the_workspace_and_sessions_do_not_change_accounts_or_memberships()
    {
        using var factory = A28TestSupport.Factory(); var password = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(factory, password, "HR");
        (string Hash, string Stamp, string Role, int Members) Snapshot(SmartCvDbContext db)
        {
            var u = db.Users.AsNoTracking().Single(x => x.UserId == user.UserId);
            return (u.PasswordHash, u.SecurityStamp, u.Role, db.WorkspaceMembers.Count(m => m.UserId == user.UserId));
        }
        (string, string, string, int) before = default;
        await A28TestSupport.WithDb(factory, db => { before = Snapshot(db); return Task.CompletedTask; });
        using var anonymous = factory.CreateClient();
        using var login = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = user.Email, Password = password });
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
        using var http = Bearer(factory, token);
        for (var i = 0; i < 3; i++) { await JobsBodyAsync(http); (await http.GetAsync("/api/auth/me")).Dispose(); }
        await A28TestSupport.WithDb(factory, async db =>
        {
            Assert.Equal(1, (await db.AuditEvents.SingleAsync(e => e.ActionCode == "auth.login")).WorkspaceId);
            var after = Snapshot(db);
            Assert.Equal(before.Item3, after.Role); Assert.Equal(before.Item4, after.Members); Assert.Equal(before.Item2, after.Stamp);   // PasswordHash có thể được rehash khi đăng nhập
        });
    }

    [Fact]
    public async Task Database_failure_while_checking_the_workspace_is_503_not_401()
    {
        var fault = new A28TestSupport.DataFault(); using var factory = A28TestSupport.Factory(fault: fault);
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        using var http = Bearer(factory, A28TestSupport.Token(user));
        fault.FailRead = true; fault.WrapReadFailure = true;
        foreach (var path in new[] { "/api/jobs", "/api/auth/me" })
        {
            using var response = await http.GetAsync(path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.DoesNotContain("SYNTHETIC", await response.Content.ReadAsStringAsync());
        }
    }

    // ---------- Validator ở mức đơn vị ----------

    private sealed class FakeStore(UserSessionState? state) : ISessionStateStore
    {
        public int? RequestedWorkspace;
        public Task<UserSessionState?> GetAsync(int userId, CancellationToken ct) => Task.FromResult(state);
        public Task<UserSessionState?> GetAsync(int userId, int? workspaceId, CancellationToken ct) { RequestedWorkspace = workspaceId; return Task.FromResult(state); }
    }

    private static ClaimsPrincipal Principal(User user, params string[] workspaceClaims)
    {
        var claims = new List<Claim>
        {
            new(SmartCvClaims.UserId, "7"), new(SmartCvClaims.Email, "u@example.invalid"), new(SmartCvClaims.Name, "U"),
            new(SmartCvClaims.Role, "HR"), new(SmartCvClaims.Stamp, "stamp"), new(SmartCvClaims.MustChangePassword, "false")
        };
        claims.AddRange(workspaceClaims.Select(w => new Claim(SmartCvClaims.Workspace, w)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", SmartCvClaims.Name, SmartCvClaims.Role));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]   // workspace khóa
    [InlineData(false, true, false)]   // hết membership
    public async Task Validator_accepts_the_workspace_only_when_membership_and_activity_hold(bool member, bool active, bool accepted)
    {
        var state = new UserSessionState(true, "stamp", "HR", false, "U", "u@example.invalid",
            member ? new WorkspaceSessionInfo(3, "WS", "Personal", "Owner") : null, active);
        var store = new FakeStore(state);
        var validator = new UserSessionValidator(store, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()), TimeProvider.System);
        var result = await validator.ValidateSessionAsync(Principal(new User(), "3"), CancellationToken.None, requireFresh: true);
        Assert.Equal(3, store.RequestedWorkspace);
        Assert.Equal(accepted, result.Error is null);
        Assert.Equal(accepted ? 3 : null, result.Workspace?.Id);
        Assert.Equal(accepted ? "Owner" : null, result.Workspace?.Role);   // vai trò lấy từ database
    }

    [Fact]
    public async Task Validator_without_a_claim_asks_for_no_workspace_and_returns_none()
    {
        var store = new FakeStore(new UserSessionState(true, "stamp", "HR", false, "U", "u@example.invalid"));
        var validator = new UserSessionValidator(store, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()), TimeProvider.System);
        var result = await validator.ValidateSessionAsync(Principal(new User()), CancellationToken.None, requireFresh: true);
        Assert.Null(result.Error); Assert.Null(result.Workspace); Assert.Null(store.RequestedWorkspace);
    }

    [Fact]
    public void Workspace_selection_prefers_internal_then_lowest_id()
    {
        var personal = new WorkspaceSessionInfo(5, "P", "Personal", "Owner");
        var internalWs = new WorkspaceSessionInfo(9, "I", "Internal", "Member");
        Assert.Equal(internalWs, WorkspaceSelectionRules.Choose([personal, internalWs]));
        Assert.Equal(personal, WorkspaceSelectionRules.Choose([new WorkspaceSessionInfo(6, "P2", "Personal", "Owner"), personal]));
        Assert.Null(WorkspaceSelectionRules.Choose([]));
    }
}
