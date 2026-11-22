
extern alias web;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Contracts;
using SmartCv.Core.Workspaces;
using web::SmartCv.Web.Services;
using web::SmartCv.Web.Helpers;

namespace SmartCv.Tests;

public sealed class B55AccountUiTests
{
    private sealed class CaptureHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        ) => handler(request);
    }

    [Fact]
    public async Task Profile_read_update_and_setup_use_A57_real_routes_and_json_body()
    {
        var calls = new List<(HttpMethod Method, string Url)>();

        using var handler = new CaptureHandler(async request =>
        {
            var route = request.RequestUri!.AbsolutePath;
            calls.Add((request.Method, route));

            if (request.Method == HttpMethod.Put)
            {
                var body = await request.Content!.ReadAsStringAsync();

                // B55: Kiểm tra giá trị JSON sau khi giải mã Unicode.
                // System.Text.Json có thể serialize ký tự tiếng Việt
                // dưới dạng \uXXXX mà vẫn hoàn toàn hợp lệ.
                using var document = JsonDocument.Parse(body);

                Assert.Equal(
                    JsonValueKind.Object,
                    document.RootElement.ValueKind
                );

                Assert.True(
                    document.RootElement.TryGetProperty(
                        "fullName",
                        out var fullName
                    )
                );

                Assert.Equal(
                    "Nguyễn Thị Minh",
                    fullName.GetString()
                );

                // Yêu cầu cập nhật hồ sơ không được gửi mật khẩu.
                Assert.DoesNotContain(
                    "password",
                    body,
                    StringComparison.OrdinalIgnoreCase
                );
            }

            var json = route.EndsWith("password-setup")
                ? "{\"message\":\"accepted\"}"
                : "{\"userId\":5,\"fullName\":\"Nguyễn Thị Minh\",\"email\":\"member@example.com\",\"emailVerified\":true,\"hasPassword\":false,\"role\":\"HR\",\"signupSource\":\"google\",\"createdAt\":\"2026-11-22T00:00:00Z\",\"lastLoginAt\":null,\"workspace\":null,\"logins\":[]}";

            return new HttpResponseMessage(
                route.EndsWith("password-setup")
                    ? HttpStatusCode.Accepted
                    : HttpStatusCode.OK
            )
            {
                Content = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json"
                )
            };
        });

        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.invalid/")
        };

        var api = new AccountApiClient(
            http,
            NullLogger<AccountApiClient>.Instance
        );

        // Kiểm tra lấy thông tin hồ sơ.
        var loaded = await api.GetProfileAsync();

        Assert.True(loaded.IsSuccess);
        Assert.NotNull(loaded.Data);
        Assert.False(loaded.Data.HasPassword);

        // Kiểm tra cập nhật họ tên có dấu tiếng Việt.
        var updated = await api.UpdateProfileAsync("Nguyễn Thị Minh");

        Assert.True(updated.IsSuccess);

        // Kiểm tra yêu cầu thiết lập mật khẩu qua email.
        var passwordSetup = await api.RequestPasswordSetupEmailAsync();

        Assert.True(passwordSetup.IsSuccess);

        // Kiểm tra đúng HTTP method và endpoint A57.
        Assert.Equal(
            new[]
            {
                (HttpMethod.Get, "/api/auth/me/profile"),
                (HttpMethod.Put, "/api/auth/me/profile"),
                (HttpMethod.Post, "/api/auth/me/password-setup")
            },
            calls
        );
    }

    [Fact]
    public async Task Workspace_client_reads_A54_contract()
    {
        using var handler = new CaptureHandler(
            _ => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"id\":3,\"name\":\"Cá nhân\",\"kind\":\"Personal\",\"role\":\"Owner\",\"dailyAiLimit\":15,\"aiUsedToday\":2,\"aiResetsAt\":\"2026-11-23T00:00:00Z\",\"maxCvCount\":100,\"cvCount\":17,\"hasActivePrompt\":true,\"jobCount\":4}",
                        Encoding.UTF8,
                        "application/json"
                    )
                }
            )
        );

        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.invalid/")
        };

        var api = new WorkspaceApiClient(
            http,
            NullLogger<WorkspaceApiClient>.Instance
        );

        var result = await api.GetWorkspaceAsync();

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Data);
        Assert.Equal(17, result.Data.CvCount);
        Assert.Equal(2, result.Data.AiUsedToday);
    }

    [Fact]
    public void Profile_name_validation_respects_server_limit()
    {
        Assert.NotNull(AccountProfileRules.Validate(""));

        Assert.NotNull(
            AccountProfileRules.Validate(new string('a', 151))
        );

        Assert.NotNull(
            AccountProfileRules.Validate("bad\u0007name")
        );

        Assert.Equal(
            "Nguyễn Văn A",
            AccountProfileRules.Normalize("  Nguyễn Văn A ")
        );

        Assert.Null(
            AccountProfileRules.Validate("Nguyễn Văn A")
        );

        Assert.Equal(
            "HR nội bộ",
            AccountProfileRules.DisplayRole("HR")
        );
    }
}
