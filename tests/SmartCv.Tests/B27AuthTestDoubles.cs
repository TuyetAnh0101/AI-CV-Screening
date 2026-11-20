extern alias web;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>B27: "sessionStorage" trong bộ nhớ. Dùng chung một instance giữa hai scope = cùng một tab được tải lại.</summary>
internal sealed class B27MemoryStore : ITokenStore
{
    public AuthSnapshot? Saved { get; set; }
    public int Loads, Saves, Clears;
    public Exception? FailLoad;

    public Task<AuthSnapshot?> LoadAsync()
    {
        Interlocked.Increment(ref Loads);
        return FailLoad is { } error ? Task.FromException<AuthSnapshot?>(error) : Task.FromResult(Saved);
    }

    public Task SaveAsync(AuthSnapshot snapshot) { Interlocked.Increment(ref Saves); Saved = snapshot; return Task.CompletedTask; }
    public Task ClearAsync() { Interlocked.Increment(ref Clears); Saved = null; return Task.CompletedTask; }
}

/// <summary>B27: API đăng nhập giả để test AuthSession / LoginForm không cần máy chủ.</summary>
internal sealed class B27FakeAuthApi : IAuthApi
{
    public Func<string, string, Task<ApiResult<LoginResponse>>> Login { get; set; } =
        (_, _) => Task.FromResult(ApiResult<LoginResponse>.Failure("not configured", 500));
    public Func<string, ApiResult<MeDto>> Me { get; set; } = _ => ApiResult<MeDto>.Failure("not configured", 500);
    public ConcurrentQueue<(string Email, string Password)> LoginCalls { get; } = new();
    public ConcurrentQueue<string> MeCalls { get; } = new();

    public Task<ApiResult<LoginResponse>> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        LoginCalls.Enqueue((email, password));
        return Login(email, password);
    }

    public Task<ApiResult<MeDto>> GetMeAsync(string token, CancellationToken ct = default)
    {
        MeCalls.Enqueue(token);
        return Task.FromResult(Me(token));
    }

    // B29: đổi mật khẩu (token, mật khẩu hiện tại, mật khẩu mới).
    public Func<string, string, string, Task<ApiResult<ChangePasswordResponse>>> ChangePassword { get; set; } =
        (_, _, _) => Task.FromResult(ApiResult<ChangePasswordResponse>.Failure("not configured", 500));
    public ConcurrentQueue<(string Token, string Current, string New)> ChangeCalls { get; } = new();

    public Task<ApiResult<ChangePasswordResponse>> ChangePasswordAsync(string token, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        ChangeCalls.Enqueue((token, currentPassword, newPassword));
        return ChangePassword(token, currentPassword, newPassword);
    }
}

/// <summary>B27: máy chủ API giả — ghi lại mọi request (đường dẫn + header Authorization) và trả lời theo hàm cấu hình.</summary>
internal sealed class B27CaptureHandler : HttpMessageHandler
{
    public sealed record Call(string Method, string PathAndQuery, string? Authorization, string? Body);

    public ConcurrentQueue<Call> Calls { get; } = new();
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; } = _ => Task.FromResult(Json(HttpStatusCode.OK, new { }));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Enqueue(new(request.Method.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString(), body));
        return await Respond(request);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Problem(HttpStatusCode status, string code) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(new { title = "t", detail = "d", status = (int)status, code }), Encoding.UTF8, "application/problem+json") };
}

internal static class B27
{
    public static MeDto User(string role = "Admin", bool mustChange = false, string name = "Nguyễn Thị Lan", int id = 7) =>
        new() { UserId = id, Email = $"user{id}@example.invalid", FullName = name, Role = role, MustChangePassword = mustChange, LastLoginAt = DateTime.UtcNow };

    public static LoginResponse Login(string token, string role = "Admin", bool mustChange = false, TimeSpan? lifetime = null) =>
        new() { Token = token, ExpiresAt = DateTime.UtcNow + (lifetime ?? TimeSpan.FromHours(8)), User = User(role, mustChange) };

    public static AuthSession Session(B27MemoryStore? store = null, B27FakeAuthApi? api = null, TimeProvider? time = null) =>
        new(store ?? new B27MemoryStore(), api ?? new B27FakeAuthApi(), time ?? TimeProvider.System, NullLogger<AuthSession>.Instance);

    /// <summary>
    /// DI thật của SmartCv.Web (AddSmartCvApiClients) với máy chủ API giả làm primary handler.
    /// <paramref name="sharedStore"/>: mọi scope dùng chung một "sessionStorage" (mô phỏng F5 cùng tab); null: mỗi scope một kho riêng.
    /// </summary>
    public static ServiceProvider WebServices(HttpMessageHandler primary, B27MemoryStore? sharedStore = null, ILoggerProvider? logs = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => { b.SetMinimumLevel(LogLevel.Trace); if (logs is not null) b.AddProvider(logs); });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ApiBaseUrl"] = "https://api.test/" }).Build();
        services.AddSmartCvApiClients(config);
        services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => primary));
        if (sharedStore is not null) services.AddSingleton<ITokenStore>(sharedStore);
        else services.AddScoped<ITokenStore, B27MemoryStore>();
        // ValidateScopes: AuthSession (Scoped) không bao giờ được lấy từ root/singleton. Không ValidateOnBuild vì
        // ProtectedSessionStorage cần IJSRuntime của circuit thật (test thay ITokenStore nên không dùng tới).
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
