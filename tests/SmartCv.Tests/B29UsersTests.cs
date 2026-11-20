extern alias web;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Tests.Auth;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>
/// B29: trang Tài khoản. Phần lớn chạy với API A30 THẬT (TestServer, InMemory, tài khoản tổng hợp) qua đúng UserApiClient của
/// Web, nên hợp đồng (X-Total-Count, If-Match, mã lỗi) được kiểm chứng thật. Lỗi mạng / 503 / 429 / phản hồi trễ dùng máy chủ giả.
/// </summary>
public class B29UsersTests
{
    // ---------- Khung: Admin tổng hợp đăng nhập thật ----------

    private sealed class AdminTab : IAsyncDisposable
    {
        public required WebApplicationFactory<Program> Api { get; init; }
        public required ServiceProvider Web { get; init; }
        public required AsyncServiceScope Scope { get; init; }
        public required User Admin { get; init; }
        public UserApiClient Users => Scope.ServiceProvider.GetRequiredService<UserApiClient>();
        public UsersWorkspace Workspace() => new(Users) { SearchDelay = TimeSpan.Zero };

        public async ValueTask DisposeAsync() { await Scope.DisposeAsync(); await Web.DisposeAsync(); Api.Dispose(); }
    }

    private static async Task<AdminTab> SignedInAdmin()
    {
        var api = A28TestSupport.Factory();
        var password = A28TestSupport.Password();
        var admin = await A28TestSupport.Seed(api, password, "Admin");
        var web = B27.WebServices(api.Server.CreateHandler());
        var scope = web.CreateAsyncScope();
        var login = await new LoginForm(TimeProvider.System) { Email = admin.Email, Password = password }.SubmitAsync(scope.ServiceProvider.GetRequiredService<IAuthApi>());
        await scope.ServiceProvider.GetRequiredService<AuthSession>().SignInAsync(login!);
        return new AdminTab { Api = api, Web = web, Scope = scope, Admin = admin };
    }

    private static async Task SeedUsers(WebApplicationFactory<Program> api, int hr, int lockedHr = 0)
    {
        await A28TestSupport.WithDb(api, async db =>
        {
            for (var i = 0; i < hr + lockedHr; i++)
                db.Users.Add(new User { Email = $"hr{i:00}@example.invalid", FullName = $"Nhân sự {i:00}", Role = "HR", IsActive = i < hr, PasswordHash = "x", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });
    }

    // ---------- Danh sách ----------

    [Fact]
    public async Task List_comes_from_the_server_with_the_real_total_and_the_system_wide_admin_count()
    {
        await using var t = await SignedInAdmin();
        await SeedUsers(t.Api, hr: 23, lockedHr: 2);
        using var w = t.Workspace();
        await w.LoadAsync();
        Assert.True(w.Loaded);
        Assert.Equal(26, w.Total);
        Assert.Equal(20, w.Items.Count);
        Assert.Equal(2, w.PageCount);
        Assert.Equal(1, w.ActiveAdmins);

        await w.GoToPageAsync(2);
        Assert.Equal(6, w.Items.Count);
        Assert.True(w.Items.Take(4).All(u => u.IsActive) && w.Items.Skip(4).All(u => !u.IsActive));   // đang hoạt động trước

        await w.FilterActiveAsync(false);
        Assert.Equal((2, 1), (w.Total!.Value, w.Page));
        await w.FilterRoleAsync("Admin");
        Assert.Equal(0, w.Total);
        Assert.True(w.HasFilters);
        await w.ClearFiltersAsync();
        Assert.Equal(26, w.Total);
    }

    [Fact]
    public async Task Search_is_done_by_the_server()
    {
        await using var t = await SignedInAdmin();
        await SeedUsers(t.Api, hr: 12);
        using var w = t.Workspace();
        await w.SearchAsync("hr07@");
        Assert.Equal("hr07@example.invalid", Assert.Single(w.Items).Email);
        Assert.Equal(1, w.Total);
    }

    [Fact]
    public async Task Stale_search_results_never_overwrite_the_latest()
    {
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new B27CaptureHandler
        {
            Respond = async request =>
            {
                var q = request.RequestUri!.Query;
                if (q.Contains("search=old")) await slow.Task;
                var who = q.Contains("search=old") ? "Cũ" : "Mới";
                return Paged(request, [Dto(7, who)], 1);
            }
        };
        using var w = new UsersWorkspace(Client(api)) { SearchDelay = TimeSpan.Zero };
        var first = w.SearchAsync("old");
        var second = w.SearchAsync("new");
        await second;
        slow.SetResult();
        await first;
        Assert.Equal("Mới", Assert.Single(w.Items).FullName);
        Assert.False(w.Loading);
    }

    [Fact]
    public async Task Debounce_sends_only_the_last_keystroke()
    {
        var api = new B27CaptureHandler { Respond = r => Task.FromResult(Paged(r, [], 0)) };
        using var w = new UsersWorkspace(Client(api)) { SearchDelay = TimeSpan.FromMilliseconds(80) };
        var a = w.SearchAsync("l"); var b = w.SearchAsync("la"); var c = w.SearchAsync("lan");
        await Task.WhenAll(a, b, c);
        var lists = api.Calls.Where(x => x.PathAndQuery.StartsWith("/api/users?search")).ToList();
        Assert.Equal("/api/users?search=lan&page=1&pageSize=20", Assert.Single(lists).PathAndQuery);
    }

    [Fact]
    public async Task Missing_total_header_is_not_invented()
    {
        var api = new B27CaptureHandler { Respond = _ => Task.FromResult(B27CaptureHandler.Json(HttpStatusCode.OK, new[] { Dto(1, "A") })) };
        using var w = new UsersWorkspace(Client(api));
        await w.LoadAsync();
        Assert.Null(w.Total);
        Assert.Null(w.PageCount);
        Assert.Null(w.ActiveAdmins);   // không biết → nút khóa / hạ quyền Admin bị khóa cho tới khi biết
    }

    [Theory]
    [InlineData(503, "Dịch vụ tài khoản tạm không khả dụng")]
    [InlineData(403, "không có quyền xem danh sách")]
    public async Task List_errors_are_explained(int status, string text)
    {
        var api = new B27CaptureHandler { Respond = _ => Task.FromResult(B27CaptureHandler.Problem((HttpStatusCode)status, "x")) };
        using var w = new UsersWorkspace(Client(api));
        await w.LoadAsync();
        Assert.False(w.Loaded);
        Assert.Contains(text, w.LoadError);
    }

    // ---------- Tạo ----------

    [Fact]
    public async Task Create_hr_shows_the_temporary_password_once_and_the_account_must_change_it()
    {
        await using var t = await SignedInAdmin();
        using var w = t.Workspace();
        await w.LoadAsync();
        w.OpenCreate();
        (w.FormName, w.FormEmail) = ("  Lê   Minh Tuấn ", "Tuan.HR@Example.Invalid");
        await w.SubmitCreateAsync();

        Assert.Equal(UserDialogKind.Credential, w.Dialog);
        var temporary = w.TemporaryPassword!;
        Assert.Equal(12, temporary.Length);
        Assert.Equal("tuan.hr@example.invalid", w.Target!.Email);
        Assert.Contains("Đã tạo tài khoản Lê Minh Tuấn", w.Message);
        var created = Assert.Single(w.Items, u => u.Email == "tuan.hr@example.invalid");
        Assert.True(created.MustChangePassword && created.IsActive && created.Role == "HR");

        Assert.True(w.CloseDialog());
        Assert.Null(w.TemporaryPassword);   // đóng là mất: không xem lại được

        // Mật khẩu tạm dùng được, và bắt đổi mật khẩu.
        var login = await t.Scope.ServiceProvider.GetRequiredService<IAuthApi>().LoginAsync("tuan.hr@example.invalid", temporary);
        Assert.True(login.IsSuccess && login.Data!.User.MustChangePassword);
    }

    [Fact]
    public async Task Duplicate_email_is_a_field_error_and_the_dialog_stays_open()
    {
        await using var t = await SignedInAdmin();
        using var w = t.Workspace();
        w.OpenCreate();
        (w.FormName, w.FormEmail) = ("Trùng", t.Admin.Email);
        await w.SubmitCreateAsync();
        Assert.Equal(UserDialogKind.Create, w.Dialog);
        Assert.Contains("đã có tài khoản", w.FieldErrors["email"]);
    }

    [Fact]
    public async Task Admin_account_needs_explicit_confirmation_and_invalid_input_sends_nothing()
    {
        await using var t = await SignedInAdmin();
        using var w = t.Workspace();
        w.OpenCreate();
        (w.FormName, w.FormEmail, w.FormRole) = ("Quản trị hai", "admin2@example.invalid", "Admin");
        await w.SubmitCreateAsync();
        Assert.True(w.FieldErrors.ContainsKey("confirmAdmin"));
        (w.FormEmail, w.ConfirmAdmin) = ("không-phải-email", true);
        await w.SubmitCreateAsync();
        Assert.True(w.FieldErrors.ContainsKey("email"));
        (w.GeneratePassword, w.InitialPassword, w.FormEmail) = (false, "ngan", "admin2@example.invalid");
        await w.SubmitCreateAsync();
        Assert.True(w.FieldErrors.ContainsKey("password"));
        await A28TestSupport.WithDb(t.Api, async db => Assert.Equal(1, await db.Users.CountAsync()));   // chưa gửi gì

        (w.GeneratePassword, w.InitialPassword) = (true, "");
        await w.SubmitCreateAsync();
        Assert.Equal(UserDialogKind.Credential, w.Dialog);
        Assert.Equal(2, w.ActiveAdmins);
    }

    [Fact]
    public async Task Create_with_unknown_outcome_does_not_retry_and_reloads_the_list()
    {
        var api = new B27CaptureHandler
        {
            Respond = request => request.Method == HttpMethod.Post
                ? throw new HttpRequestException("reset")
                : Task.FromResult(Paged(request, [], 0))
        };
        using var w = new UsersWorkspace(Client(api));
        w.OpenCreate();
        (w.FormName, w.FormEmail) = ("Lan", "lan@example.invalid");
        await w.SubmitCreateAsync();
        Assert.Contains("CÓ THỂ đã được tạo", w.DialogError);
        Assert.Single(api.Calls, c => c.Method == "POST");
        Assert.Contains(api.Calls, c => c.Method == "GET");   // đã tải lại để kiểm tra
    }

    [Fact]
    public async Task Rate_limit_is_explained_with_the_wait_time()
    {
        var api = new B27CaptureHandler
        {
            Respond = _ =>
            {
                var r = B27CaptureHandler.Problem(HttpStatusCode.TooManyRequests, "credential_rate_limited");
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(37));
                return Task.FromResult(r);
            }
        };
        using var w = new UsersWorkspace(Client(api));
        w.OpenCreate();
        (w.FormName, w.FormEmail) = ("Lan", "lan@example.invalid");
        await w.SubmitCreateAsync();
        Assert.Contains("37 giây", w.DialogError);
    }

    // ---------- Sửa (If-Match) ----------

    [Fact]
    public async Task Edit_uses_the_viewed_version_and_a_conflict_never_overwrites()
    {
        await using var t = await SignedInAdmin();
        await SeedUsers(t.Api, hr: 1);
        using var w = t.Workspace();
        await w.LoadAsync();
        var hr = w.Items.Single(u => u.Role == "HR");
        w.OpenEdit(hr);
        w.FormName = "Tên mới của tôi";

        // Admin khác đổi tài khoản này trước (đổi SecurityStamp → Version khác).
        await A28TestSupport.WithDb(t.Api, async db => { var row = await db.Users.SingleAsync(u => u.UserId == hr.UserId); row.FullName = "Admin khác vừa sửa"; row.SecurityStamp = User.NewStamp(); await db.SaveChangesAsync(); });
        await w.SubmitEditAsync(t.Admin.UserId);
        Assert.True(w.NeedsReload);
        Assert.Contains("CHƯA được thực hiện", w.DialogError);
        await w.SubmitEditAsync(t.Admin.UserId);   // bị chặn tới khi tải lại
        Assert.Equal("Admin khác vừa sửa", (await A30AccountEndpointTests.Saved(t.Api, hr.UserId)).FullName);

        await w.ReloadTargetAsync();
        Assert.False(w.NeedsReload);
        Assert.Equal("Admin khác vừa sửa", w.FormName);     // hiện dữ liệu mới nhất, không giữ bản cũ để ghi đè
        w.FormName = "Tên mới của tôi";
        await w.SubmitEditAsync(t.Admin.UserId);
        Assert.Equal(UserDialogKind.None, w.Dialog);
        Assert.Equal("Tên mới của tôi", (await A30AccountEndpointTests.Saved(t.Api, hr.UserId)).FullName);
    }

    [Fact]
    public async Task Promoting_to_admin_needs_confirmation_and_unchanged_edits_send_nothing()
    {
        await using var t = await SignedInAdmin();
        await SeedUsers(t.Api, hr: 1);
        using var w = t.Workspace();
        await w.LoadAsync();
        var hr = w.Items.Single(u => u.Role == "HR");
        w.OpenEdit(hr);
        await w.SubmitEditAsync(t.Admin.UserId);
        Assert.Equal("Chưa có thay đổi nào để lưu.", w.DialogError);
        w.FormRole = "Admin";
        await w.SubmitEditAsync(t.Admin.UserId);
        Assert.True(w.FieldErrors.ContainsKey("confirmAdmin"));
        w.ConfirmAdmin = true;
        await w.SubmitEditAsync(t.Admin.UserId);
        Assert.Contains("phải đăng nhập lại", w.Message);
        Assert.Equal("Admin", (await A30AccountEndpointTests.Saved(t.Api, hr.UserId)).Role);
    }

    [Fact]
    public async Task Own_role_cannot_be_changed_from_the_ui()
    {
        await using var t = await SignedInAdmin();
        using var w = t.Workspace();
        await w.LoadAsync();
        var me = w.Items.Single(u => u.UserId == t.Admin.UserId);
        w.OpenEdit(me);
        w.FormRole = "HR";
        await w.SubmitEditAsync(t.Admin.UserId);
        Assert.Contains("Không tự đổi vai trò", w.FieldErrors["role"]);
        Assert.Equal("Admin", (await A30AccountEndpointTests.Saved(t.Api, t.Admin.UserId)).Role);
    }

    // ---------- Khóa / mở khóa ----------

    [Fact]
    public async Task Lock_and_unlock_hr_with_if_match_and_unlock_keeps_the_password_state()
    {
        await using var t = await SignedInAdmin();
        await SeedUsers(t.Api, hr: 1);
        await A28TestSupport.WithDb(t.Api, async db => { (await db.Users.SingleAsync(u => u.Role == "HR")).MustChangePassword = true; await db.SaveChangesAsync(); });
        using var w = t.Workspace();
        await w.LoadAsync();
        var hr = w.Items.Single(u => u.Role == "HR");

        w.OpenLock(hr, t.Admin.UserId);
        Assert.Equal(UserDialogKind.Lock, w.Dialog);
        await w.ConfirmLockAsync();
        Assert.Contains("Đã khóa", w.Message);
        var locked = await A30AccountEndpointTests.Saved(t.Api, hr.UserId);
        Assert.False(locked.IsActive);

        var row = w.Items.Single(u => u.UserId == hr.UserId);
        w.OpenUnlock(row);
        await w.ConfirmUnlockAsync();
        var unlocked = await A30AccountEndpointTests.Saved(t.Api, hr.UserId);
        Assert.True(unlocked.IsActive);
        Assert.True(unlocked.MustChangePassword);              // mở khóa không đổi cờ
        Assert.Equal(locked.PasswordHash, unlocked.PasswordHash); // và không đổi mật khẩu
    }

    [Fact]
    public async Task Self_lock_is_blocked_in_the_ui_before_any_request()
    {
        await using var t = await SignedInAdmin();
        using var w = t.Workspace();
        await w.LoadAsync();
        var me = w.Items.Single(u => u.UserId == t.Admin.UserId);
        Assert.Equal("Không tự khóa tài khoản của chính mình.", UserText.LockBlockedReason(me, t.Admin.UserId, w.ActiveAdmins));
        w.OpenLock(me, t.Admin.UserId);
        Assert.Equal(UserDialogKind.None, w.Dialog);
    }

    [Fact]
    public void Last_active_admin_rules_match_core_and_unknown_count_is_fail_closed()
    {
        var other = Dto(9, "Admin khác", role: "Admin");
        Assert.Equal("Không khóa Admin cuối cùng đang hoạt động.", UserText.LockBlockedReason(other, 1, activeAdmins: 1));
        Assert.Null(UserText.LockBlockedReason(other, 1, activeAdmins: 2));
        Assert.NotNull(UserText.LockBlockedReason(other, 1, activeAdmins: null));
        Assert.Contains("Admin cuối cùng", UserText.RoleBlockedReason(other, "HR", 1, activeAdmins: 1));
        Assert.Null(UserText.RoleBlockedReason(Dto(5, "HR"), "Admin", 1, activeAdmins: null));   // nâng quyền HR không cần đếm
        Assert.Null(UserText.LockBlockedReason(Dto(5, "Đã khóa", active: false), 1, 1));
    }

    [Fact]
    public async Task Server_side_admin_safety_is_explained_without_raw_text()
    {
        var api = new B27CaptureHandler { Respond = r => Task.FromResult(r.Method == HttpMethod.Get ? Paged(r, [Dto(9, "Admin khác", role: "Admin")], 1, admins: 2) : B27CaptureHandler.Problem(HttpStatusCode.Conflict, "admin_safety")) };
        using var w = new UsersWorkspace(Client(api));
        await w.LoadAsync();
        w.OpenLock(w.Items[0], 1);
        await w.ConfirmLockAsync();
        Assert.Contains("ít nhất một Admin đang hoạt động", w.DialogError);
        Assert.Equal(UserDialogKind.Lock, w.Dialog);
    }

    // ---------- Đặt lại mật khẩu ----------

    [Fact]
    public async Task Reset_is_hr_only_and_shows_a_new_temporary_password_once()
    {
        await using var t = await SignedInAdmin();
        await SeedUsers(t.Api, hr: 1);
        using var w = t.Workspace();
        await w.LoadAsync();
        var me = w.Items.Single(u => u.Role == "Admin");
        w.OpenReset(me);
        Assert.Equal(UserDialogKind.None, w.Dialog);   // không có đặt lại cho Admin

        var hr = w.Items.Single(u => u.Role == "HR");
        w.OpenReset(hr);
        await w.ConfirmResetAsync();
        Assert.Equal(UserDialogKind.Credential, w.Dialog);
        var temporary = w.TemporaryPassword!;
        var saved = await A30AccountEndpointTests.Saved(t.Api, hr.UserId);
        Assert.True(saved.MustChangePassword);
        Assert.Equal(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success,
            new Microsoft.AspNetCore.Identity.PasswordHasher<User>().VerifyHashedPassword(saved, saved.PasswordHash, temporary));
        w.CloseDialog();
        Assert.Null(w.TemporaryPassword);

        // Gọi thẳng API cho Admin (không qua giao diện) vẫn bị A30 chặn, và câu báo rõ ràng.
        var direct = await t.Users.ResetPasswordAsync(me);
        Assert.Equal(("admin_password_reset_forbidden", 409), (direct.ErrorCode, direct.StatusCode!.Value));
        Assert.Contains("Không đặt lại mật khẩu cho tài khoản Admin", UsersWorkspace.WriteError(direct));
    }

    [Fact]
    public async Task Double_click_on_reset_sends_one_request()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new B27CaptureHandler
        {
            Respond = async r =>
            {
                if (r.Method == HttpMethod.Get) return Paged(r, [Dto(5, "Lan")], 1, admins: 1);
                await gate.Task;
                return B27CaptureHandler.Json(HttpStatusCode.OK, new UserCredentialResponse { User = Dto(5, "Lan"), TemporaryPassword = "Abcdefgh2345" });
            }
        };
        using var w = new UsersWorkspace(Client(api));
        await w.LoadAsync();
        w.OpenReset(w.Items[0]);
        var first = w.ConfirmResetAsync();
        await w.ConfirmResetAsync();
        Assert.False(w.CloseDialog());   // đang gửi: không đóng được
        gate.SetResult();
        await first;
        Assert.Single(api.Calls, c => c.Method == "POST");
        Assert.Equal("Abcdefgh2345", w.TemporaryPassword);
    }

    [Theory]
    [InlineData(401, "Phiên đăng nhập đã hết hạn")]
    [InlineData(403, "không có quyền quản lý tài khoản")]
    [InlineData(500, "CÓ THỂ đã được thực hiện")]
    [InlineData(503, "CÓ THỂ đã được thực hiện")]
    public async Task Write_errors_are_specific(int status, string text)
    {
        var api = new B27CaptureHandler { Respond = r => Task.FromResult(r.Method == HttpMethod.Get ? Paged(r, [Dto(5, "Lan")], 1, admins: 1) : B27CaptureHandler.Problem((HttpStatusCode)status, "x")) };
        using var w = new UsersWorkspace(Client(api));
        await w.LoadAsync();
        w.OpenUnlock(Dto(5, "Lan", active: false));
        await w.ConfirmUnlockAsync();
        Assert.Contains(text, w.DialogError);
        Assert.Single(api.Calls, c => c.Method == "POST");   // không tự gửi lại
    }

    [Fact]
    public async Task Writes_send_if_match_with_the_viewed_version()
    {
        var seen = new ConcurrentQueue<string?>();
        var handler = new HeaderProbe(seen);
        var client = new UserApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") }, NullLogger<UserApiClient>.Instance);
        var target = Dto(5, "Lan");
        await client.LockAsync(target); await client.UnlockAsync(target); await client.ResetPasswordAsync(target); await client.UpdateAsync(target, "Lan", "HR");
        Assert.All(seen, v => Assert.Equal($"\"{target.Version}\"", v));
        Assert.Equal(4, seen.Count);
    }

    [Theory]
    [InlineData(null, null, null, 1, 20, "api/users?page=1&pageSize=20")]
    [InlineData(" lan ", "HR", false, 2, 50, "api/users?search=lan&role=HR&isActive=false&page=2&pageSize=50")]
    [InlineData("a&b=c", null, true, 1, 20, "api/users?search=a%26b%3Dc&isActive=true&page=1&pageSize=20")]
    public void Query_string_is_built_safely(string? search, string? role, bool? active, int page, int size, string expected) =>
        Assert.Equal(expected, new UserQuery(search, role, active, page, size).ToUrl());

    // ---------- Hỗ trợ ----------

    private sealed class HeaderProbe(ConcurrentQueue<string?> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            seen.Enqueue(request.Headers.TryGetValues("If-Match", out var v) ? v.Single() : null);
            return Task.FromResult(B27CaptureHandler.Json(HttpStatusCode.OK, new UserCredentialResponse { User = Dto(5, "Lan") }));
        }
    }

    private static UserApiClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") }, NullLogger<UserApiClient>.Instance);

    internal static UserDto Dto(int id, string name, string role = "HR", bool active = true) => new()
    {
        UserId = id, FullName = name, Email = $"u{id}@example.invalid", Role = role, RoleName = role, IsActive = active,
        Version = new string((char)('a' + id % 6), 64), CreatedAt = DateTime.UtcNow
    };

    /// <summary>Phản hồi danh sách kèm X-Total-Count. Yêu cầu đếm Admin (role=Admin&amp;isActive=true) trả <paramref name="admins"/>.</summary>
    internal static HttpResponseMessage Paged(HttpRequestMessage? request, UserDto[] items, int total, int admins = 1)
    {
        var adminCount = request?.RequestUri?.Query is { } q && q.Contains("role=Admin") && q.Contains("isActive=true") && q.Contains("pageSize=1&") is false && q.EndsWith("pageSize=1");
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(adminCount ? [] : items, JsonSerializerOptions.Web), Encoding.UTF8, "application/json") };
        r.Headers.Add("X-Total-Count", (adminCount ? admins : total).ToString());
        return r;
    }
}
