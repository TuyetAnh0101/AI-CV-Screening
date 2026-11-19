extern alias web;

using System.Net;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Contracts;
using SmartCv.Tests.Auth;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>
/// B27: đăng ký DI thật của SmartCv.Web (AddSmartCvApiClients) + máy chủ API giả. Kiểm tra token được gắn lên từng request
/// của đúng scope (circuit), không bỏ sót client nghiệp vụ nào, và xử lý 401/403 không lặp.
/// </summary>
public class B27BearerTests
{
    private static async Task<AsyncServiceScope> SignedInScope(ServiceProvider services, string token, string role = "Admin")
    {
        var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuthSession>().SignInAsync(B27.Login(token, role));
        return scope;
    }

    private sealed class FakeFile(string name, byte[] bytes) : IBrowserFile
    {
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => bytes.Length;
        public string ContentType => "application/pdf";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) => new MemoryStream(bytes);
    }

    [Fact]
    public async Task Every_business_client_sends_the_session_Bearer_token()
    {
        var api = new B27CaptureHandler();
        await using var services = B27.WebServices(api);
        await using var scope = await SignedInScope(services, "token-A");
        var sp = scope.ServiceProvider;

        // Các trang đang lỗi 401: Dashboard, Jobs, Runs, CV Library, Candidates
        await sp.GetRequiredService<DashboardApiClient>().GetSummaryAsync(new());
        await sp.GetRequiredService<DashboardApiClient>().GetTrendAsync(new());
        await sp.GetRequiredService<JobApiClient>().GetJobsAsync(activeOnly: false);
        await sp.GetRequiredService<JobApiClient>().GetJobsAsync(activeOnly: true);
        await sp.GetRequiredService<RunApiClient>().GetLatestAsync();
        await sp.GetRequiredService<RunApiClient>().ListScreeningAsync(50);
        await sp.GetRequiredService<RunApiClient>().ListPageAsync(new());
        await sp.GetRequiredService<CvApiClient>().ListAsync(new CvLibraryFilter());
        await sp.GetRequiredService<CandidateApiClient>().ListPeopleAsync(new());
        // Các client nghiệp vụ còn lại, cả GET / POST / PUT / PATCH / DELETE và các đường gửi tự dựng request
        await sp.GetRequiredService<CandidateApiClient>().CreateAsync(new CandidateRequest { FullName = "A", Email = "a@b.vn" });
        await sp.GetRequiredService<ApplicationApiClient>().GetStageOptionsAsync();
        await sp.GetRequiredService<ApplicationApiClient>().ChangeStageAsync(1, new ChangeStageRequest());
        await sp.GetRequiredService<EvaluationApiClient>().GetAsync(1);
        await sp.GetRequiredService<EvaluationApiClient>().CheckCvAsync(Guid.NewGuid());
        await sp.GetRequiredService<PromptApiClient>().GetVersionsAsync();
        await sp.GetRequiredService<PromptApiClient>().ClearLabelAsync(1, 2);
        await sp.GetRequiredService<AiPreviewApiClient>().PreviewAsync(1, new AiPreviewRequest { PromptVersionId = 1 });
        await sp.GetRequiredService<JobApiClient>().UpdateAsync(1, new JobUpsertRequest());

        var calls = api.Calls.ToList();
        Assert.Equal(18, calls.Count);
        Assert.All(calls, c => Assert.Equal("Bearer token-A", c.Authorization));
        foreach (var path in new[] { "/api/dashboard/summary", "/api/dashboard/trend", "/api/jobs?activeOnly=false", "/api/jobs?activeOnly=true",
                     "/api/runs/latest", "/api/runs?type=Screening&take=50", "/api/cvs?", "/api/candidates?" })
            Assert.Contains(calls, c => c.PathAndQuery.StartsWith(path, StringComparison.Ordinal));
        Assert.Contains(calls, c => c.Method == "PATCH");
        Assert.Contains(calls, c => c.Method == "DELETE");
        Assert.Contains(calls, c => c.Method == "PUT");
    }

    [Fact]
    public async Task Multipart_CV_upload_still_streams_the_file_and_carries_the_token()
    {
        var api = new B27CaptureHandler();
        await using var services = B27.WebServices(api);
        await using var scope = await SignedInScope(services, "token-A");
        var cvs = scope.ServiceProvider.GetRequiredService<CvApiClient>();
        await cvs.UploadAsync([new FakeFile("cv.pdf", "%PDF-1.4 B27-FILE-BYTES"u8.ToArray())], "Email", consentConfirmed: true);
        var call = Assert.Single(api.Calls);
        Assert.Equal("/api/cvs/upload", call.PathAndQuery);
        Assert.Equal("Bearer token-A", call.Authorization);
        Assert.Contains("B27-FILE-BYTES", call.Body);
        Assert.Contains("consentConfirmed", call.Body);
    }

    [Fact]
    public async Task Login_and_health_never_send_a_Bearer_token_even_when_signed_in()
    {
        var api = new B27CaptureHandler { Respond = r => Task.FromResult(B27CaptureHandler.Json(HttpStatusCode.OK, new { status = "ok", time = DateTime.UtcNow })) };
        await using var services = B27.WebServices(api);
        await using var scope = await SignedInScope(services, "token-A");
        await scope.ServiceProvider.GetRequiredService<HealthApiClient>().IsApiOnlineAsync();
        await scope.ServiceProvider.GetRequiredService<IAuthApi>().LoginAsync("a@b.vn", "Secret-123");
        Assert.Equal(2, api.Calls.Count);
        Assert.All(api.Calls, c => Assert.Null(c.Authorization));
    }

    [Fact]
    public async Task Two_circuits_never_mix_tokens_even_under_parallel_load()
    {
        var api = new B27CaptureHandler { Respond = async r => { await Task.Delay(Random.Shared.Next(0, 5)); return B27CaptureHandler.Json(HttpStatusCode.OK, new { }); } };
        await using var services = B27.WebServices(api);
        await using var a = await SignedInScope(services, "token-A", "Admin");
        await using var b = await SignedInScope(services, "token-B", "HR");
        var jobsA = a.ServiceProvider.GetRequiredService<JobApiClient>();
        var jobsB = b.ServiceProvider.GetRequiredService<JobApiClient>();
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => i % 2 == 0 ? jobsA.GetJobAsync(1) : jobsB.GetJobAsync(2)));
        Assert.Equal(40, api.Calls.Count);
        Assert.All(api.Calls, c => Assert.Equal(c.PathAndQuery == "/api/jobs/1" ? "Bearer token-A" : "Bearer token-B", c.Authorization));
    }

    [Fact]
    public async Task Signed_out_tab_sends_no_business_request_at_all()
    {
        var api = new B27CaptureHandler();
        await using var services = B27.WebServices(api);
        await using var scope = services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<JobApiClient>().GetJobsAsync();
        var write = await scope.ServiceProvider.GetRequiredService<CandidateApiClient>().CreateAsync(new CandidateRequest());
        Assert.Empty(api.Calls);
        Assert.Equal(ApiFailureKind.SignedOut, result.FailureKind);
        Assert.Equal(401, result.StatusCode);
        Assert.Equal("authentication_required", result.ErrorCode);
        Assert.False(write.IsSuccess);
    }

    [Fact]
    public async Task After_logout_the_old_token_is_never_sent_again()
    {
        var api = new B27CaptureHandler();
        await using var services = B27.WebServices(api);
        await using var scope = await SignedInScope(services, "token-A");
        var jobs = scope.ServiceProvider.GetRequiredService<JobApiClient>();
        await jobs.GetJobsAsync();
        await scope.ServiceProvider.GetRequiredService<AuthSession>().SignOutAsync();
        var after = await jobs.GetJobsAsync();
        Assert.Single(api.Calls);
        Assert.Equal(ApiFailureKind.SignedOut, after.FailureKind);
    }

    [Fact]
    public async Task A_401_ends_the_session_once_and_later_calls_are_not_retried()
    {
        var api = new B27CaptureHandler { Respond = _ => Task.FromResult(B27CaptureHandler.Problem(HttpStatusCode.Unauthorized, "authentication_required")) };
        var store = new B27MemoryStore();
        await using var services = B27.WebServices(api, store);
        await using var scope = await SignedInScope(services, "token-A");
        var session = scope.ServiceProvider.GetRequiredService<AuthSession>();
        var expired = 0; session.Expired += () => expired++;
        var dashboard = scope.ServiceProvider.GetRequiredService<DashboardApiClient>();

        var first = await dashboard.GetSummaryAsync(new());
        var rest = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => dashboard.GetTrendAsync(new())));

        Assert.Equal(401, first.StatusCode);
        Assert.Single(api.Calls);                                    // không gửi lại, không bão 401
        Assert.All(rest, r => Assert.Equal(ApiFailureKind.SignedOut, r.FailureKind));
        Assert.Equal(1, expired);
        Assert.False(session.IsSignedIn);
        Assert.Equal(AuthEndReason.Expired, session.EndReason);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task Concurrent_401s_raise_Expired_exactly_once()
    {
        var release = new TaskCompletionSource();
        var api = new B27CaptureHandler { Respond = async _ => { await release.Task; return B27CaptureHandler.Problem(HttpStatusCode.Unauthorized, "authentication_required"); } };
        await using var services = B27.WebServices(api);
        await using var scope = await SignedInScope(services, "token-A");
        var session = scope.ServiceProvider.GetRequiredService<AuthSession>();
        var expired = 0; session.Expired += () => Interlocked.Increment(ref expired);
        var jobs = scope.ServiceProvider.GetRequiredService<JobApiClient>();
        var pending = Enumerable.Range(0, 6).Select(i => jobs.GetJobAsync(i)).ToList();
        await Task.Delay(50);
        release.SetResult();
        await Task.WhenAll(pending);
        Assert.Equal(6, api.Calls.Count);
        Assert.Equal(1, expired);
    }

    [Fact]
    public async Task A_late_401_for_an_old_token_does_not_end_a_newer_session()
    {
        var release = new TaskCompletionSource();
        var api = new B27CaptureHandler
        {
            Respond = async r => r.Headers.Authorization?.Parameter == "token-old"
                ? await release.Task.ContinueWith(_ => B27CaptureHandler.Problem(HttpStatusCode.Unauthorized, "authentication_required"))
                : B27CaptureHandler.Json(HttpStatusCode.OK, new { })
        };
        await using var services = B27.WebServices(api);
        await using var scope = await SignedInScope(services, "token-old");
        var session = scope.ServiceProvider.GetRequiredService<AuthSession>();
        var slow = scope.ServiceProvider.GetRequiredService<JobApiClient>().GetJobsAsync();
        await session.SignInAsync(B27.Login("token-new"));           // đăng nhập lại trong lúc request cũ còn chạy
        release.SetResult();
        await slow;
        Assert.True(session.IsSignedIn);
        await scope.ServiceProvider.GetRequiredService<JobApiClient>().GetJobsAsync();
        Assert.Equal("Bearer token-new", api.Calls.Last().Authorization);
    }

    [Fact]
    public async Task A_403_is_kept_as_403_and_does_not_sign_the_user_out()
    {
        var api = new B27CaptureHandler { Respond = _ => Task.FromResult(B27CaptureHandler.Problem(HttpStatusCode.Forbidden, "access_denied")) };
        await using var services = B27.WebServices(api);
        await using var scope = await SignedInScope(services, "token-A", "HR");
        var session = scope.ServiceProvider.GetRequiredService<AuthSession>();
        var expired = 0; session.Expired += () => expired++;
        var result = await scope.ServiceProvider.GetRequiredService<JobApiClient>().CreateAsync(new JobUpsertRequest());
        Assert.Equal(403, result.StatusCode);
        Assert.Equal("access_denied", result.ErrorCode);
        Assert.Equal("d", result.ErrorMessage);                      // nội dung lỗi vẫn đọc được sau khi đã xem mã
        Assert.True(session.IsSignedIn);
        Assert.False(session.MustChangePassword);
        Assert.Equal(0, expired);
    }

    [Fact]
    public async Task Password_change_required_403_flags_the_session_without_ending_it()
    {
        var api = new B27CaptureHandler { Respond = _ => Task.FromResult(B27CaptureHandler.Problem(HttpStatusCode.Forbidden, "password_change_required")) };
        await using var services = B27.WebServices(api);
        await using var scope = await SignedInScope(services, "token-A", "HR");
        var session = scope.ServiceProvider.GetRequiredService<AuthSession>();
        var result = await scope.ServiceProvider.GetRequiredService<CvApiClient>().ListAsync(new CvLibraryFilter());
        Assert.Equal(403, result.StatusCode);
        Assert.True(session.IsSignedIn);
        Assert.True(session.MustChangePassword);
    }

    [Fact]
    public async Task Refresh_in_the_same_tab_restores_via_api_auth_me_and_keeps_using_the_token()
    {
        var api = new B27CaptureHandler
        {
            Respond = r => Task.FromResult(r.RequestUri!.AbsolutePath == "/api/auth/me"
                ? B27CaptureHandler.Json(HttpStatusCode.OK, B27.User("HR"))
                : B27CaptureHandler.Json(HttpStatusCode.OK, new { }))
        };
        var tab = new B27MemoryStore();
        await using var services = B27.WebServices(api, tab);
        await using (await SignedInScope(services, "token-A")) { }        // circuit 1 đăng nhập rồi đóng (F5)

        await using var circuit2 = services.CreateAsyncScope();
        await circuit2.ServiceProvider.GetRequiredService<JobApiClient>().GetJobsAsync();
        var session = circuit2.ServiceProvider.GetRequiredService<AuthSession>();
        Assert.True(session.IsSignedIn); Assert.True(session.IsVerified);
        Assert.Equal(["/api/auth/me", "/api/jobs?activeOnly=true"], api.Calls.Select(c => c.PathAndQuery));
        Assert.All(api.Calls, c => Assert.Equal("Bearer token-A", c.Authorization));
    }

    [Fact]
    public async Task AuthSession_is_scoped_per_circuit_and_never_resolved_from_the_root()
    {
        await using var services = B27.WebServices(new B27CaptureHandler());
        await using var a = services.CreateAsyncScope();
        await using var b = services.CreateAsyncScope();
        Assert.NotSame(a.ServiceProvider.GetRequiredService<AuthSession>(), b.ServiceProvider.GetRequiredService<AuthSession>());
        Assert.Same(a.ServiceProvider.GetRequiredService<AuthSession>(), a.ServiceProvider.GetRequiredService<AuthSession>());
        Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<AuthSession>());
    }

    [Fact]
    public async Task Password_and_token_never_appear_in_logs_even_at_Trace_level()
    {
        var logs = new A28TestSupport.LogCapture();
        var api = new B27CaptureHandler
        {
            Respond = r => Task.FromResult(r.RequestUri!.AbsolutePath == "/api/auth/login"
                ? B27CaptureHandler.Json(HttpStatusCode.OK, B27.Login("TOKEN-SECRET-B27"))
                : B27CaptureHandler.Problem(HttpStatusCode.Unauthorized, "authentication_required"))
        };
        await using var services = B27.WebServices(api, logs: logs);
        await using var scope = services.CreateAsyncScope();
        var form = new LoginForm(TimeProvider.System) { Email = "lan@example.vn", Password = "PASSWORD-SECRET-B27" };
        var response = await form.SubmitAsync(scope.ServiceProvider.GetRequiredService<IAuthApi>());
        await scope.ServiceProvider.GetRequiredService<AuthSession>().SignInAsync(response!);
        await scope.ServiceProvider.GetRequiredService<JobApiClient>().GetJobsAsync();   // 401 → log cảnh báo
        Assert.NotEmpty(logs.Messages);
        Assert.DoesNotContain(logs.Messages, m => m.Contains("TOKEN-SECRET-B27") || m.Contains("PASSWORD-SECRET-B27") || m.Contains("lan@example.vn"));
    }
}
