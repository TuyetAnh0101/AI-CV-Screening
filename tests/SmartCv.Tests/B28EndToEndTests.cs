extern alias web;

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Tests.Auth;
using web::SmartCv.Web.Components.Pages;
using JobsPage = web::SmartCv.Web.Components.Pages.Jobs;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>
/// B28 ↔ A28/A29/A30 thật (TestServer, InMemory, tài khoản tổng hợp — không SQL Server, không tài khoản thật):
/// vai trò mà giao diện dùng đến từ phản hồi đăng nhập và /api/auth/me thật; bảng quyền trang khớp với API.
/// </summary>
public class B28EndToEndTests
{
    private static async Task<(AsyncServiceScope Scope, AuthSession Session)> Login(ServiceProvider web, User user, string password)
    {
        var scope = web.CreateAsyncScope();
        var form = new LoginForm(TimeProvider.System) { Email = user.Email, Password = password };
        var response = await form.SubmitAsync(scope.ServiceProvider.GetRequiredService<IAuthApi>());
        Assert.NotNull(response);
        var session = scope.ServiceProvider.GetRequiredService<AuthSession>();
        await session.SignInAsync(response!);
        return (scope, session);
    }

    private static async Task<bool> CanOpen(IServiceProvider sp, Type page, ClaimsPrincipal user)
    {
        var policy = await AuthorizationPolicy.CombineAsync(sp.GetRequiredService<IAuthorizationPolicyProvider>(),
            page.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>());
        return (await sp.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, null, policy!)).Succeeded;
    }

    private static Task<AuthenticationState> State(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync();

    [Theory]
    [InlineData("Admin", AccessLevel.Admin, true)]
    [InlineData("HR", AccessLevel.Hr, false)]
    public async Task Real_login_and_refresh_give_the_role_that_decides_pages_and_actions(string role, AccessLevel level, bool admin)
    {
        using var api = A28TestSupport.Factory();
        var password = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(api, password, role);
        var tab = new B27MemoryStore();
        await using var web = B27.WebServices(api.Server.CreateHandler(), tab);

        var (first, _) = await Login(web, user, password);
        await using (first)
        {
            var u = (await State(first)).User;
            Assert.Equal(level, AccessRules.Level(u));
            // #114: business pages/actions belong to the HR business operator only; the System Admin gets admin pages instead.
            Assert.Equal(!admin, await CanOpen(first.ServiceProvider, typeof(JobsPage), u));
            Assert.Equal(!admin, await CanOpen(first.ServiceProvider, typeof(JobEdit), u));
            Assert.Equal(!admin, AccessRules.Can(u, AppPermission.ManageJobs));
            Assert.Equal(admin, AccessRules.Can(u, AppPermission.ManageUsers));
            Assert.Equal(admin, AccessRules.Can(u, AppPermission.ViewAudit));
        }

        // F5 cùng tab: phiên đã lưu được xác minh lại bằng /api/auth/me thật trước khi có vai trò.
        await using var refreshed = web.CreateAsyncScope();
        var again = (await State(refreshed)).User;
        Assert.Equal(level, AccessRules.Level(again));
        Assert.True(refreshed.ServiceProvider.GetRequiredService<AuthSession>().IsVerified);
    }

    [Fact]
    public async Task Hr_hitting_an_admin_api_gets_403_access_denied_and_stays_signed_in()
    {
        using var api = A28TestSupport.Factory();
        var password = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(api, password, "HR");
        await using var web = B27.WebServices(api.Server.CreateHandler());
        var (scope, session) = await Login(web, user, password);
        await using var _ = scope;
        var changed = 0; scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>().AuthenticationStateChanged += _ => changed++;

        // Quản lý tài khoản vẫn chỉ Admin; HR gọi trực tiếp bị chặn nhưng phiên KHÔNG bị đăng xuất.
        var result = await scope.ServiceProvider.GetRequiredService<UserApiClient>().ListAsync(new());
        Assert.Equal(403, result.StatusCode);
        Assert.Equal("access_denied", result.ErrorCode);
        Assert.True(session.IsSignedIn);
        Assert.Equal(0, changed);
        Assert.Equal(AccessLevel.Hr, AccessRules.Level((await State(scope)).User));
    }

    [Fact]
    public async Task Real_Web_clients_of_a_System_Admin_are_refused_business_writes_and_stay_signed_in()
    {
        using var api = B43AuditTests.Factory();
        var password = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(api, password, "Admin");
        await using var web = B27.WebServices(api.Server.CreateHandler());
        var (scope, session) = await Login(web, user, password);
        await using var lifetime = scope;
        var title = "Synthetic Web permissions " + Guid.NewGuid().ToString("N");
        var job = await scope.ServiceProvider.GetRequiredService<JobApiClient>().CreateAsync(new JobUpsertRequest { Title = title });
        Assert.Equal(403, job.StatusCode);
        Assert.Equal("access_denied", job.ErrorCode);
        var set = await scope.ServiceProvider.GetRequiredService<PromptApiClient>().CreateEvaluationSetAsync(new CreateEvaluationSetRequest { Name = title });
        Assert.Equal(403, set.StatusCode);
        Assert.True(session.IsSignedIn);
        await A28TestSupport.WithDb(api, async db => Assert.False(await db.Jobs.IgnoreQueryFilters().AnyAsync(j => j.Title == title)));
    }

    [Theory]
    [InlineData("HR")]
    public async Task Real_Web_clients_can_write_the_business_resources_shown_by_the_UI(string role)
    {
        using var api = B43AuditTests.Factory();
        var password = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(api, password, role);
        await using var web = B27.WebServices(api.Server.CreateHandler());
        var (scope, session) = await Login(web, user, password);
        await using var lifetime = scope;
        var jobs = scope.ServiceProvider.GetRequiredService<JobApiClient>();
        var job = await jobs.CreateAsync(new JobUpsertRequest { Title = "Synthetic Web permissions " + Guid.NewGuid().ToString("N") });
        Assert.True(job.IsSuccess, job.ErrorMessage);
        var stopped = await jobs.DeactivateAsync(job.Data!.JobId);
        Assert.True(stopped.IsSuccess, stopped.ErrorMessage);
        Assert.False(stopped.Data!.IsActive);
        var prompts = scope.ServiceProvider.GetRequiredService<PromptApiClient>();
        var version = await prompts.CreateVersionAsync(new CreatePromptVersionRequest
            { VersionName = Guid.NewGuid().ToString("N"), SystemPrompt = "Synthetic Web assessment rules." });
        Assert.True(version.IsSuccess, version.ErrorMessage);
        var set = await prompts.CreateEvaluationSetAsync(new CreateEvaluationSetRequest { Name = "Synthetic Web set " + Guid.NewGuid().ToString("N") });
        Assert.True(set.IsSuccess, set.ErrorMessage);
        Assert.True(session.IsVerified);
        Assert.True(AccessRules.CanUseApp((await State(scope)).User));
    }

    [Fact]
    public async Task Role_change_by_an_admin_revokes_the_old_session_on_the_next_refresh()
    {
        using var api = A28TestSupport.Factory();
        var password = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(api, password, "Admin");
        var tab = new B27MemoryStore();
        await using var web = B27.WebServices(api.Server.CreateHandler(), tab);
        var (first, _) = await Login(web, user, password);
        await first.DisposeAsync();

        // A30: đổi vai trò = đổi SecurityStamp → token cũ hết hiệu lực ngay.
        await A28TestSupport.WithDb(api, async db =>
        {
            var row = await db.Users.SingleAsync(u => u.UserId == user.UserId);
            row.Role = "HR";
            row.SecurityStamp = Guid.NewGuid().ToString("N");
            await db.SaveChangesAsync();
        });

        await using var refreshed = web.CreateAsyncScope();
        var state = await State(refreshed);
        Assert.Equal(AccessLevel.Anonymous, AccessRules.Level(state.User));        // không giữ menu / quyền Admin cũ
        Assert.Equal(AuthEndReason.Expired, refreshed.ServiceProvider.GetRequiredService<AuthSession>().EndReason);
        Assert.Null(tab.Saved);
    }
}
