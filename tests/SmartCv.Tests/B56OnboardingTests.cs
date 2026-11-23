extern alias web;
using SmartCv.Core.Workspaces;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using web::SmartCv.Web.Services;
using web::SmartCv.Web.Helpers;

namespace SmartCv.Tests;

public sealed class B56OnboardingTests
{
    private static WorkspaceDto Personal(int jobs, int cvs, bool activePrompt = false) =>
        new(9, "Cá nhân", "Personal", "Owner", 30, 0,
            new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.FromHours(7)),
            100, cvs, activePrompt, jobs);

    [Fact]
    public async Task Sample_job_client_uses_real_A54_POST_route()
    {
        HttpMethod? method = null;
        string? path = null;
        using var handler = new Probe(async r =>
        {
            method = r.Method;
            path = r.RequestUri?.AbsolutePath;
            await Task.CompletedTask;
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"jobId\":42,\"title\":\"Mẫu\",\"isActive\":true}", Encoding.UTF8, "application/json")
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.invalid/") };
        var api = new WorkspaceApiClient(http, NullLogger<WorkspaceApiClient>.Instance);
        var result = await api.CreateSampleJobAsync();
        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Data!.JobId);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/api/workspace/sample-job", path);
    }

    private sealed class Probe(Func<HttpRequestMessage,Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handler(request);
    }

    [Fact] public void Empty_personal_shows_first_steps() =>
        Assert.True(B56OnboardingRules.Show(Personal(0, 0), false, false, false));

    [Fact] public void Internal_never_shows() =>
        Assert.False(B56OnboardingRules.Show(Personal(0, 0) with { Kind = "Internal" }, false, true, false));

    [Fact] public void Hidden_stays_hidden() =>
        Assert.False(B56OnboardingRules.Show(Personal(0, 0), false, true, true));

    [Fact] public void Completed_workspace_not_shown_unless_welcome() =>
        Assert.False(B56OnboardingRules.Show(Personal(1, 2), true, false, false));

    [Fact] public void Welcome_forces_intro() =>
        Assert.True(B56OnboardingRules.Show(Personal(1, 2), true, true, false));

    [Fact] public void Steps_use_real_counts_and_run() =>
        Assert.Equal(2, B56OnboardingRules.CompletedSteps(Personal(1, 3), null));
}
