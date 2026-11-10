extern alias web;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;
using web::SmartCv.Web.Components.Pages;
using web::SmartCv.Web.Models;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

// Dispatch actual Blazor events without an external browser or an additional test package.
#pragma warning disable BL0006 // RenderTree APIs used only by this test renderer.
public class JobSelectionUiTests
{
    private sealed class Handler(Func<Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/api/jobs?activeOnly=true", request.RequestUri!.PathAndQuery);
            return respond();
        }
    }

    private sealed class TestRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        public int RootId { get; private set; }
        public Task StartAsync() => Dispatcher.InvokeAsync(async () =>
        {
            RootId = AssignRootComponentId(InstantiateComponent(typeof(Upload)));
            await RenderRootComponentAsync(RootId);
        });
        public List<RenderTreeFrame> Frames(int? id = null)
        {
            var frames = GetCurrentRenderTreeFrames(id ?? RootId);
            var all = frames.Array.Take(frames.Count).ToList();
            return all.SelectMany(f => f.FrameType == RenderTreeFrameType.Component
                ? new[] { f }.Concat(Frames(f.ComponentId)) : [f]).ToList();
        }
        public string Text => System.Text.RegularExpressions.Regex.Replace(WebUtility.HtmlDecode(string.Concat(
            Frames().Select(f => f.FrameType switch
            {
                RenderTreeFrameType.Text => f.TextContent,
                RenderTreeFrameType.Markup => System.Text.RegularExpressions.Regex.Replace(f.MarkupContent, "<[^>]+>", " "),
                _ => " "
            }))), @"\s+", " ");
        public Task ChangeJobAsync(string value) => Dispatcher.InvokeAsync(() => DispatchEventAsync(
            Frames().First(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "onchange").AttributeEventHandlerId,
            null, new ChangeEventArgs { Value = value }));
    }

    private static ServiceProvider Services(Func<Task<HttpResponseMessage>> respond)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ApiBaseUrl"] = "https://api.test/" }).Build());
        services.Configure<CvUploadOptions>(_ => { });
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, NoJs>();
        services.AddSingleton(new JobApiClient(new HttpClient(new Handler(respond)) { BaseAddress = new Uri("https://api.test/") }, NullLogger<JobApiClient>.Instance));
        services.AddSingleton(new CvApiClient(new HttpClient(), Microsoft.Extensions.Options.Options.Create(new CvUploadOptions()), NullLogger<CvApiClient>.Instance));
        // B6: trang Upload inject thêm 3 service này (test B4 không gọi tới)
        services.AddSingleton(new ApplicationApiClient(new HttpClient(), NullLogger<ApplicationApiClient>.Instance));
        services.AddSingleton(new RunApiClient(new HttpClient(), NullLogger<RunApiClient>.Instance));
        services.AddSingleton<NavigationManager>(new TestNavigationManager());
        return services.BuildServiceProvider();
    }

    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }

    private static HttpResponseMessage Jobs(params JobDto[] jobs) => new(HttpStatusCode.OK) { Content = JsonContent.Create(jobs) };

    [Fact]
    public async Task Changing_job_updates_required_skills_gpa_experience_without_clearing_CVs()
    {
        using var services = Services(() => Task.FromResult(Jobs(
            new() { JobId = 1, Title = "Backend", IsActive = true, MinGpa = 2.5m, MinYearsExperience = .5m,
                Skills = [new() { DisplayName = "C#", SkillType = "MustHave" }, new() { DisplayName = "Optional Docker", SkillType = "NiceToHave" }] },
            new() { JobId = 2, Title = "Tester", IsActive = true },
            new() { JobId = 3, Title = "Closed position", IsActive = false })));
        await using var renderer = new TestRenderer(services);
        await renderer.StartAsync();
        Assert.DoesNotContain("Closed position", renderer.Text);
        Assert.DoesNotContain("GPA tối thiểu", renderer.Text);
        var items = (List<UploadItem>)renderer.Frames().First(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "Items").AttributeValue;
        var item = new UploadItem { FileName = "preserved.pdf", CvId = Guid.NewGuid(), State = UploadItemState.Uploaded };
        items.Add(item);

        await renderer.ChangeJobAsync("1");
        Assert.Contains("C#", renderer.Text);
        Assert.DoesNotContain("Optional Docker", renderer.Text);
        Assert.Contains("GPA tối thiểu 2.50", renderer.Text);
        Assert.Contains("Kinh nghiệm từ 0.5 năm", renderer.Text);
        Assert.Contains("preserved.pdf", renderer.Text);
        await renderer.ChangeJobAsync("2");
        Assert.DoesNotContain("C#", renderer.Text);
        Assert.Contains("Không yêu cầu GPA", renderer.Text);
        Assert.Contains("Không yêu cầu", renderer.Text);
        Assert.Contains("Kinh nghiệm từ 0 năm", renderer.Text);
        await renderer.ChangeJobAsync("");
        Assert.DoesNotContain("Kỹ năng bắt buộc:", renderer.Text);
        Assert.Same(item, Assert.Single(items));
        Assert.True(item.Selected);
    }

    [Fact]
    public async Task Empty_jobs_show_message_and_keep_B3_upload_available()
    {
        using var services = Services(() => Task.FromResult(Jobs()));
        await using var renderer = new TestRenderer(services);
        await renderer.StartAsync();
        Assert.Contains("Chưa có công việc đang tuyển.", renderer.Text);
        Assert.Contains("Tải CV lên để phân tích", renderer.Text);
    }

    [Fact]
    public async Task Failed_jobs_can_be_retried_without_reloading_page()
    {
        var attempts = 0;
        using var services = Services(() => ++attempts == 1
            ? throw new HttpRequestException("API unavailable")
            : Task.FromResult(Jobs(new JobDto { JobId = 1, Title = "Recovered job", IsActive = true })));
        await using var renderer = new TestRenderer(services);
        await renderer.StartAsync();
        Assert.Contains("Không kết nối được máy chủ API", renderer.Text);
        // The first two onclick handlers are the existing tab buttons; select the retry button by its text.
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var frames = renderer.Frames();
            var retryIndex = frames.FindIndex(f => (f.FrameType == RenderTreeFrameType.Text && f.TextContent.Contains("Thử lại"))
                || (f.FrameType == RenderTreeFrameType.Markup && f.MarkupContent.Contains("Thử lại")));
            var handler = frames.Take(retryIndex).Last(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "onclick");
            await renderer.DispatchEventAsync(handler.AttributeEventHandlerId, null, new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        });
        Assert.Equal(2, attempts);
        Assert.Contains("Recovered job", renderer.Text);
        Assert.DoesNotContain("Không kết nối được máy chủ API", renderer.Text);
    }
}
#pragma warning restore BL0006
