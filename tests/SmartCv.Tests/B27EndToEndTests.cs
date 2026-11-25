extern alias web;

using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Contracts;
using SmartCv.Tests.Auth;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>
/// B27 ↔ A28/A29 thật: SmartCv.Api chạy trong TestServer với JwtBearer, chính sách A29 và ProblemDetails thật (InMemory,
/// dữ liệu tổng hợp, không SQL Server). SmartCv.Web dùng đúng DI của nó; chỉ "sessionStorage" là bản trong bộ nhớ.
/// Chứng minh: chưa đăng nhập thì Web không gửi request; đăng nhập xong các API của Dashboard/Jobs/Runs/CV/Ứng viên
/// không còn 401; phiên bị thu hồi thì về trạng thái chưa đăng nhập đúng một lần.
/// </summary>
public class B27EndToEndTests
{
    private static ServiceProvider Web(WebApplicationFactory<Program> api, B27MemoryStore? tab = null) =>
        B27.WebServices(api.Server.CreateHandler(), tab);

    private static async Task<(AsyncServiceScope Scope, LoginForm Form)> LoginAsync(ServiceProvider web, string email, string password)
    {
        var scope = web.CreateAsyncScope();
        var form = new LoginForm(TimeProvider.System) { Email = email, Password = password };
        var response = await form.SubmitAsync(scope.ServiceProvider.GetRequiredService<IAuthApi>());
        if (response is not null) await scope.ServiceProvider.GetRequiredService<AuthSession>().SignInAsync(response);
        return (scope, form);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("HR")]
    public async Task After_real_login_the_previously_401_pages_are_authorized(string role)
    {
        using var api = A28TestSupport.Factory();
        var password = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(api, password, role);
        await using var web = Web(api);

        // Trước khi đăng nhập: Web không gửi gì (không còn hàng loạt 401 trong log API).
        await using (var anonymous = web.CreateAsyncScope())
        {
            var before = await anonymous.ServiceProvider.GetRequiredService<JobApiClient>().GetJobsAsync(false);
            Assert.Equal(ApiFailureKind.SignedOut, before.FailureKind);
        }

        var (scope, form) = await LoginAsync(web, user.Email, password);
        await using var _ = scope;
        Assert.Null(form.FormError);
        var sp = scope.ServiceProvider;
        Assert.Equal(role, sp.GetRequiredService<AuthSession>().User!.Role);
        var savedToken = ((B27MemoryStore)sp.GetRequiredService<ITokenStore>()).Saved!.Token;   // "sessionStorage" của tab

        var results = new (string Name, int? Status, ApiFailureKind Kind)[]
        {
            Map("dashboard/summary", await sp.GetRequiredService<DashboardApiClient>().GetSummaryAsync(new())),
            Map("dashboard/trend", await sp.GetRequiredService<DashboardApiClient>().GetTrendAsync(new())),
            Map("jobs?activeOnly=false", await sp.GetRequiredService<JobApiClient>().GetJobsAsync(false)),
            Map("jobs?activeOnly=true", await sp.GetRequiredService<JobApiClient>().GetJobsAsync(true)),
            Map("runs/latest", await sp.GetRequiredService<RunApiClient>().GetLatestAsync()),
            Map("runs?type=Screening&take=50", await sp.GetRequiredService<RunApiClient>().ListScreeningAsync(50)),
            Map("runs?page=1&pageSize=20", await sp.GetRequiredService<RunApiClient>().ListPageAsync(new())),
            Map("cvs (CV Library)", await sp.GetRequiredService<CvApiClient>().ListAsync(new CvLibraryFilter())),
            Map("candidates (Candidate List)", await sp.GetRequiredService<CandidateApiClient>().ListPeopleAsync(new())),
            Map("auth/me", await sp.GetRequiredService<IAuthApi>().GetMeAsync(savedToken)),
        };
        Assert.All(results, r => Assert.NotEqual(ApiFailureKind.SignedOut, r.Kind)); // never a forced sign-out
        if (role == "Admin")
        {
            // Since #114 (B57 R1) the System Admin keeps a valid session but every business API is 403, not 401.
            Assert.All(results.Where(r => r.Name != "auth/me"), r => Assert.True(r.Status == 403, $"{r.Name} → {r.Status}"));
            Assert.Null(results.Single(r => r.Name == "auth/me").Status);
            Assert.True(sp.GetRequiredService<AuthSession>().IsSignedIn);
            return;
        }
        Assert.All(results, r => Assert.False(r.Status is 401 or 403, $"{r.Name} → {r.Status}"));
        // Danh sách (không phụ thuộc dữ liệu) phải thành công hẳn.
        Assert.All(results.Where(r => r.Name.StartsWith("jobs") || r.Name.StartsWith("cvs") || r.Name.StartsWith("candidates") || r.Name.Contains("page=") || r.Name == "auth/me"),
            r => Assert.True(r.Status is null && r.Kind == ApiFailureKind.None, $"{r.Name} → {r.Status} {r.Kind}"));
        Assert.True(sp.GetRequiredService<AuthSession>().IsSignedIn);

        static (string, int?, ApiFailureKind) Map<T>(string name, ApiResult<T> r) => (name, r.IsSuccess ? null : r.StatusCode, r.FailureKind);
    }

    [Fact]
    public async Task Revoked_account_mid_session_ends_the_session_once_and_stops_calling()
    {
        using var api = A28TestSupport.Factory();
        var password = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(api, password, "HR");
        await using var web = Web(api);
        var (scope, _) = await LoginAsync(web, user.Email, password);
        await using var _s = scope;
        var session = scope.ServiceProvider.GetRequiredService<AuthSession>();
        var expired = 0; session.Expired += () => expired++;
        var jobs = scope.ServiceProvider.GetRequiredService<JobApiClient>();
        Assert.True((await jobs.GetJobsAsync()).IsSuccess);

        // Admin khóa / đổi SecurityStamp (A29: có hiệu lực ngay ở request kế tiếp).
        await A28TestSupport.WithDb(api, async db =>
        {
            var row = await db.Users.SingleAsync(u => u.UserId == user.UserId);
            row.SecurityStamp = Guid.NewGuid().ToString("N");
            await db.SaveChangesAsync();
        });

        var revoked = await jobs.GetJobsAsync();
        var after = await jobs.GetJobsAsync();
        Assert.Equal(401, revoked.StatusCode);
        Assert.Equal("authentication_required", revoked.ErrorCode);
        Assert.Equal(ApiFailureKind.SignedOut, after.FailureKind);   // không gửi lại
        Assert.Equal(1, expired);
        Assert.False(session.IsSignedIn);
    }

    [Fact]
    public async Task Refresh_in_the_same_tab_is_verified_by_the_real_api_auth_me()
    {
        using var api = A28TestSupport.Factory();
        var password = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(api, password, "HR"); // /jobs is a business (HR) API since #114
        var tab = new B27MemoryStore();
        await using var web = Web(api, tab);
        var (first, _) = await LoginAsync(web, user.Email, password);
        await first.DisposeAsync();

        await using var circuit2 = web.CreateAsyncScope();
        var session = circuit2.ServiceProvider.GetRequiredService<AuthSession>();
        await session.EnsureRestoredAsync();
        Assert.True(session.IsSignedIn); Assert.True(session.IsVerified);
        Assert.Equal(user.Email, session.User!.Email);
        Assert.True((await circuit2.ServiceProvider.GetRequiredService<JobApiClient>().GetJobsAsync(false)).IsSuccess);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_show_the_same_message()
    {
        using var api = A28TestSupport.Factory();
        var password = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(api, password, "HR");
        await using var web = Web(api);
        var (a, wrong) = await LoginAsync(web, user.Email, password + "x");
        var (b, unknown) = await LoginAsync(web, "khong-ton-tai@example.invalid", password);
        await a.DisposeAsync(); await b.DisposeAsync();
        Assert.Equal(AuthText.InvalidCredentials, wrong.FormError);
        Assert.Equal(wrong.FormError, unknown.FormError);
    }

    [Fact]
    public async Task Locked_account_gets_the_locked_message_and_no_session()
    {
        using var api = A28TestSupport.Factory();
        var password = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(api, password, "HR", active: false);
        await using var web = Web(api);
        var (scope, form) = await LoginAsync(web, user.Email, password);
        await using var _ = scope;
        Assert.Equal("Tài khoản này đã bị khóa. Liên hệ quản trị viên (Admin) nếu cần mở lại.", form.FormError);
        Assert.False(scope.ServiceProvider.GetRequiredService<AuthSession>().IsSignedIn);
    }

    [Fact]
    public async Task Login_rate_limit_from_the_real_API_blocks_the_form_with_Retry_After()
    {
        using var api = A28TestSupport.Factory(new() { ["Auth:LoginAttemptsPerMinute"] = "2" });
        await using var web = Web(api);
        await using var scope = web.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthApi>();
        var form = new LoginForm(TimeProvider.System) { Email = "a@example.invalid" };
        for (var i = 0; i < 3 && !form.IsRateLimited; i++) { form.Password = "Wrong-pass-" + i; await form.SubmitAsync(auth); }
        Assert.True(form.IsRateLimited);
        Assert.InRange(form.RetrySecondsLeft, 1, 60);
        Assert.False(form.CanSubmit);
    }

    [Fact]
    public async Task Must_change_password_account_signs_in_but_business_403_does_not_sign_out()
    {
        using var api = A28TestSupport.Factory();
        var password = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(api, password, "HR", mustChange: true);
        await using var web = Web(api);
        var (scope, form) = await LoginAsync(web, user.Email, password);
        await using var _ = scope;
        var session = scope.ServiceProvider.GetRequiredService<AuthSession>();
        Assert.Null(form.FormError);
        Assert.True(session.MustChangePassword);
        var cvs = await scope.ServiceProvider.GetRequiredService<CvApiClient>().ListAsync(new CvLibraryFilter());
        Assert.Equal(403, cvs.StatusCode);
        Assert.Equal("password_change_required", cvs.ErrorCode);
        Assert.True(session.IsSignedIn);
    }

    [Fact]
    public async Task HR_calling_an_Admin_API_gets_403_access_denied_and_stays_signed_in()
    {
        using var api = A28TestSupport.Factory();
        var password = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(api, password, "HR");
        await using var web = Web(api);
        var (scope, _) = await LoginAsync(web, user.Email, password);
        await using var _s = scope;
        var result = await scope.ServiceProvider.GetRequiredService<UserApiClient>().ListAsync(new());
        Assert.Equal(403, result.StatusCode);
        Assert.Equal("access_denied", result.ErrorCode);
        Assert.True(scope.ServiceProvider.GetRequiredService<AuthSession>().IsSignedIn);
    }

    [Fact]
    public async Task API_not_running_shows_a_connection_message_not_a_password_error()
    {
        var down = new B27CaptureHandler { Respond = _ => throw new HttpRequestException("connection refused") };
        await using var web = B27.WebServices(down);
        await using var scope = web.CreateAsyncScope();
        var form = new LoginForm(TimeProvider.System) { Email = "a@example.invalid", Password = "Secret-123" };
        await form.SubmitAsync(scope.ServiceProvider.GetRequiredService<IAuthApi>());
        Assert.Equal("Không kết nối được máy chủ SCApp. Kiểm tra kết nối mạng hoặc thử lại sau ít phút.", form.FormError);
    }
}
