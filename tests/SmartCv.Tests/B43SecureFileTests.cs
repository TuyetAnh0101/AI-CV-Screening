extern alias web;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

public class B43SecureFileTests
{
    [Theory]
    [InlineData("application/pdf", true)]
    [InlineData("image/png", true)]
    [InlineData("image/jpeg", true)]
    [InlineData("image/svg+xml", false)]
    [InlineData("text/html", false)]
    [InlineData("application/json", false)]
    public void Only_safe_CV_types_can_be_previewed(string type, bool allowed) => Assert.Equal(allowed, FileDownloadService.CanPreview(type));

    [Fact]
    public async Task File_download_uses_current_Bearer_and_streams_without_buffering_then_disposes_response()
    {
        var content = new StreamingContent();
        var handler = new B27CaptureHandler { Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }) };
        await using var provider = B27.WebServices(handler); await using var scope = provider.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<AuthSession>(); var token = Guid.NewGuid().ToString("N"); await session.SignInAsync(B27.Login(token));
        var result = await scope.ServiceProvider.GetRequiredService<CvApiClient>().GetFileAsync(Guid.NewGuid()); Assert.True(result.IsSuccess);
        Assert.Equal(0, content.Stream.Reads); Assert.Equal(0, content.Serializations);
        Assert.Equal("Hồ sơ thử nghiệm.pdf", result.Data!.FileName);
        Assert.True(Assert.Single(handler.Calls).Authorization == "Bearer " + token);
        Assert.DoesNotContain(token, handler.Calls.Single().PathAndQuery);
        await using (var file = result.Data) { var buffer = new byte[4]; Assert.Equal(4, await file.Content.ReadAsync(buffer)); }
        Assert.True(content.Disposed); Assert.True(content.Stream.Disposed);
    }

    [Fact]
    public async Task Oversized_files_are_rejected_before_reading_and_the_response_is_disposed()
    {
        var content = new StreamingContent(); content.Headers.ContentLength = DownloadedFile.MaxBytes + 1;
        var handler = new B27CaptureHandler { Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }) };
        await using var provider = B27.WebServices(handler); await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuthSession>().SignInAsync(B27.Login(Guid.NewGuid().ToString("N")));
        var result = await scope.ServiceProvider.GetRequiredService<CvApiClient>().GetFileAsync(Guid.NewGuid());
        Assert.Equal(413, result.StatusCode); Assert.True(content.Disposed); Assert.Equal(0, content.Stream.Reads);
    }

    [Fact]
    public async Task Signed_out_and_401_file_requests_follow_the_existing_session_contract()
    {
        var handler = new B27CaptureHandler { Respond = _ => Task.FromResult(B27CaptureHandler.Problem(HttpStatusCode.Unauthorized, "authentication_required")) };
        await using var provider = B27.WebServices(handler); await using var scope = provider.CreateAsyncScope();
        var api = scope.ServiceProvider.GetRequiredService<CvApiClient>();
        Assert.Equal(401, (await api.GetFileAsync(Guid.NewGuid())).StatusCode); Assert.Empty(handler.Calls);
        var session = scope.ServiceProvider.GetRequiredService<AuthSession>(); await session.SignInAsync(B27.Login(Guid.NewGuid().ToString("N")));
        Assert.Equal(401, (await api.GetFileAsync(Guid.NewGuid())).StatusCode); Assert.False(session.IsSignedIn); Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Candidate_export_has_no_actor_or_token_in_query_and_two_circuits_do_not_share_Bearer()
    {
        var handler = new B27CaptureHandler { Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamingContent() }) };
        await using var provider = B27.WebServices(handler); await using var a = provider.CreateAsyncScope(); await using var b = provider.CreateAsyncScope();
        var first = Guid.NewGuid().ToString("N"); var second = Guid.NewGuid().ToString("N");
        await a.ServiceProvider.GetRequiredService<AuthSession>().SignInAsync(B27.Login(first)); await b.ServiceProvider.GetRequiredService<AuthSession>().SignInAsync(B27.Login(second));
        var results = await Task.WhenAll(a.ServiceProvider.GetRequiredService<CandidateApiClient>().ExportAsync(12), b.ServiceProvider.GetRequiredService<CandidateApiClient>().ExportAsync(34));
        Assert.True(handler.Calls.Single(c => c.PathAndQuery.Contains("/12/")).Authorization == "Bearer " + first);
        Assert.True(handler.Calls.Single(c => c.PathAndQuery.Contains("/34/")).Authorization == "Bearer " + second);
        Assert.All(handler.Calls, call => Assert.DoesNotContain('?', call.PathAndQuery));
        foreach (var result in results) { Assert.True(result.IsSuccess); await result.Data!.DisposeAsync(); }
    }

    [Fact]
    public async Task Actor_facade_ignores_manual_names_and_clears_with_the_session()
    {
        var session = B43UiTestSupport.Session("Người thực hiện thật"); var state = new HrActorState(session);
        await state.EnsureLoadedAsync(); await state.SetAsync("Admin giả mạo");
        Assert.Equal("Người thực hiện thật", state.Actor); Assert.Equal(session.User!.UserId, state.UserId);
        await session.SignOutAsync(); Assert.False(state.HasActor); Assert.Null(state.UserId); Assert.Null(state.Actor);
    }

    [Fact]
    public async Task File_button_HTML_has_no_Bearer_API_URL_or_editable_identity()
    {
        var session = B43UiTestSupport.Session(); var privacy = new AnonymityState(); await privacy.SetAsync(false);
        var token = Guid.NewGuid().ToString("N"); await session.SignInAsync(B27.Login(token));
        var services = new ServiceCollection().AddLogging().AddSingleton(session).AddSingleton(privacy)
            .AddSingleton<NavigationManager>(new Navigation()).AddSingleton<IJSRuntime>(new NoJs()).AddSingleton<FileDownloadService>()
            .AddSingleton(new CvApiClient(new HttpClient(new B27CaptureHandler()) { BaseAddress = new("https://api.test/") }, Options.Create(new CvUploadOptions()), NullLogger<CvApiClient>.Instance));
        await using var provider = services.BuildServiceProvider(); await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<web::SmartCv.Web.Components.Shared.CvFileButton>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["CvId"] = Guid.NewGuid() }))).ToHtmlString());
        Assert.False(html.Contains(token, StringComparison.Ordinal)); Assert.DoesNotContain("api/cvs", html); Assert.DoesNotContain("Bearer", html); Assert.Contains("Tải về", WebUtility.HtmlDecode(html));
        var source = File.ReadAllText(TestSupport.RepoFile("src/SmartCv.Web/wwwroot/js/smartcv-files.js"));
        foreach (var unsafeText in new[] { "window.open", "innerHTML", "console.", "localStorage", "Bearer", "Authorization" }) Assert.DoesNotContain(unsafeText, source);
        Assert.Contains("revokeObjectURL", source); Assert.Contains("pagehide", source);
    }

    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("https://app.test/", "https://app.test/candidates");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
    }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
    private sealed class TrackingStream() : MemoryStream(new byte[16])
    {
        internal int Reads; internal bool Disposed;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { Reads++; return base.ReadAsync(buffer, ct); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class StreamingContent : HttpContent
    {
        internal readonly TrackingStream Stream = new(); internal int Serializations; internal bool Disposed;
        public StreamingContent() { Headers.ContentType = new MediaTypeHeaderValue("application/pdf"); Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = "Hồ sơ thử nghiệm.pdf" }; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(Stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) => Task.FromResult<Stream>(Stream);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) { Serializations++; throw new InvalidOperationException("File was buffered."); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
