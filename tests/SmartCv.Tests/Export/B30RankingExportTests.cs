extern alias web;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SmartCv.Core.Contracts;
using SmartCv.Core.Export;
using SmartCv.Infrastructure.Export;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests.Export;

public class B30RankingExportTests
{
    private static RankingExportContext Context(bool anonymous = true) => new(31, 17, "Kỹ sư phần mềm", "Completed", "New", RankingFilter.Pass, "", true, true, anonymous, RankingSort.AiDesc);

    [Theory]
    [InlineData(RankingExportScope.Shortlisted, "Shortlisted", "all", false)]
    [InlineData(RankingExportScope.All, "all", "all", false)]
    [InlineData(RankingExportScope.Current, "New", "pass", true)]
    public void Scope_is_explicit_preserves_Run_Job_and_only_current_uses_table_filters(RankingExportScope scope, string stage, string fit, bool flags)
    {
        var (query, error) = Context().Build(scope, true);
        Assert.Null(error); Assert.Equal(17, query!.JobId); Assert.Equal("xlsx", query.Format);
        Assert.Equal(stage, query.Stage); Assert.Equal(fit, query.Fit); Assert.Equal(flags, query.NeedsReview); Assert.Equal(flags, query.ManualFlags); Assert.True(query.Anonymous);
        Assert.Null(query.Search); Assert.DoesNotContain("sort", RankingExportContext.QueryString(query));
    }

    [Theory]
    [InlineData(RankingFilter.All, "all")]
    [InlineData(RankingFilter.Pass, "pass")]
    [InlineData(RankingFilter.NotPass, "notpass")]
    [InlineData(RankingFilter.Unscored, "unscored")]
    public void Every_AI_filter_matches_A31_semantics(RankingFilter filter, string fit)
    {
        var context = Context() with { Fit = filter, Stage = null, NeedsReview = false, ManualFlags = false };
        var query = context.Build(RankingExportScope.Current, true).Query!;
        Assert.Equal(fit, query.Fit);
        var rows = new[] { new RankingItemDto { Status = "Completed", FinalScore = 90, PredictedPass = true }, new RankingItemDto { Status = "Completed", FinalScore = 0, PredictedPass = false }, new RankingItemDto { Status = "Failed", PredictedPass = true } };
        Assert.Equal(rows.Where(r => RankingView.MatchesFilter(r, filter)), query.Apply(rows));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Search_privacy_mismatch_blocks_current_scope_instead_of_changing_the_matching_population(bool tableAnonymous, bool fileAnonymous)
    {
        var context = Context(tableAnonymous) with { Search = tableAnonymous ? "HS12" : "Nguyễn Văn" };
        var (query, error) = context.Build(RankingExportScope.Current, fileAnonymous);
        Assert.Null(query); Assert.Contains("phải khớp", error);
        Assert.NotNull(context.Build(RankingExportScope.All, fileAnonymous).Query);
        Assert.NotNull(context.Build(RankingExportScope.Shortlisted, fileAnonymous).Query);
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Queued")]
    [InlineData("Failed")]
    public void Incomplete_runs_are_blocked_before_a_request(string status) => Assert.Null((Context() with { RunStatus = status }).Build(RankingExportScope.All, true).Query);

    [Fact]
    public void Unsupported_or_oversized_filters_fail_closed_and_private_search_is_encoded()
    {
        Assert.Null((Context() with { Search = new string('x', 101) }).Build(RankingExportScope.Current, true).Query);
        Assert.Null((Context() with { Stage = "unknown" }).Build(RankingExportScope.Current, true).Query);
        Assert.Null((Context() with { Fit = (RankingFilter)999 }).Build(RankingExportScope.Current, true).Query);
        Assert.Null((Context() with { JobId = 0 }).Build(RankingExportScope.All, true).Query);
        var query = (Context(false) with { Search = "Đặng & Bình+" }).Build(RankingExportScope.Current, false).Query!;
        Assert.Contains("search=%C4%90%E1%BA%B7ng%20%26%20B%C3%ACnh%2B", RankingExportContext.QueryString(query));
        Assert.False(query.Anonymous);
    }

    [Fact]
    public void Anonymous_search_uses_the_same_alias_as_the_table_and_preserves_original_order_and_ranks()
    {
        var rows = new List<RankingItemDto> { new() { ApplicationId = 10, Rank = 2 }, new() { ApplicationId = 11, Rank = 2 }, new() { ApplicationId = 12 } };
        foreach (var row in rows) Assert.Equal(AnonymityState.Alias(row.Rank, row.ApplicationId), RankingExportQuery.Alias(row.Rank, row.ApplicationId));
        var query = (Context() with { Stage = null, Fit = RankingFilter.All, NeedsReview = false, ManualFlags = false, Search = "#2" }).Build(RankingExportScope.Current, true).Query!;
        Assert.Equal(rows.Take(2), query.Apply(rows)); Assert.Equal(new int?[] { 2, 2 }, query.Apply(rows).Select(r => r.Rank));
    }

    [Theory]
    [InlineData("HR")]
    [InlineData("Admin")]
    public async Task Clients_use_existing_JWT_and_exact_preview_download_query_and_server_file_name(string role)
    {
        var query = Context().Build(RankingExportScope.Shortlisted, true).Query!;
        var handler = new B27CaptureHandler { Respond = request => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/settings")
            ? B27CaptureHandler.Json(HttpStatusCode.OK, new ExportSettingsDto { MaxRows = 5000 })
            : request.RequestUri.AbsolutePath.EndsWith("/preview") ? B27CaptureHandler.Json(HttpStatusCode.OK, new RankingExportPreviewDto(31, 17, "Completed", 4, 2, 2, 5000, true, "Qua vòng CV", true, null, null))
            : Excel()) };
        using var services = B27.WebServices(handler); using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AuthSession>().SignInAsync(B27.Login("test-token", role));
        var api = scope.ServiceProvider.GetRequiredService<ExportApiClient>();
        var settings = await api.SettingsAsync(); Assert.False(settings.Data!.PdfEnabled); Assert.Equal(new[] { "xlsx" }, settings.Data.Formats);
        var preview = await api.PreviewAsync(31, query); Assert.True(preview.Data!.CanExport);
        var result = await api.DownloadAsync(31, query); Assert.True(result.IsSuccess);
        await using var file = result.Data!;
        Assert.Equal("Xếp hạng lượt 31.xlsx", file.FileName); Assert.Equal(ExcelRankingWriter.ContentType, file.ContentType);
        var data = new MemoryStream(); await file.Content.CopyToAsync(data); Assert.Equal("PK-data", Encoding.UTF8.GetString(data.ToArray()));
        var calls = handler.Calls.ToArray(); Assert.Equal(3, calls.Length);
        Assert.All(calls, c => Assert.Equal("Bearer test-token", c.Authorization));
        Assert.Equal(calls[1].PathAndQuery.Replace("/preview?", "?"), calls[2].PathAndQuery);
        Assert.Contains("/api/runs/31/ranking/export?jobId=17", calls[2].PathAndQuery);
        Assert.Contains("stage=Shortlisted", calls[2].PathAndQuery); Assert.DoesNotContain(calls, c => c.PathAndQuery.Contains("format=pdf"));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(503)]
    public async Task File_errors_are_localized_and_only_401_ends_the_session(int status)
    {
        var handler = new B27CaptureHandler { Respond = _ => Task.FromResult(B27CaptureHandler.Problem((HttpStatusCode)status, "synthetic_failure")) };
        using var services = B27.WebServices(handler); using var scope = services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<AuthSession>(); await session.SignInAsync(B27.Login("test"));
        var result = await scope.ServiceProvider.GetRequiredService<ExportApiClient>().DownloadAsync(31, Context().Build(RankingExportScope.All, true).Query!);
        Assert.False(result.IsSuccess); Assert.Equal(status, result.StatusCode); Assert.Equal(status != 401, session.IsSignedIn);
        var text = RankingExportContext.Error(result); Assert.DoesNotContain("synthetic_failure", text); Assert.DoesNotContain("SQL", text); Assert.NotEmpty(text);
    }

    [Fact]
    public async Task An_explicit_anonymous_workbook_download_uses_StreamReference_without_relaxing_CV_preview_privacy()
    {
        var session = B27.Session(); await session.SignInAsync(B27.Login("test"));
        var privacy = new AnonymityState(); Assert.True(privacy.Enabled);
        var js = new StreamJs(); await using var files = new FileDownloadService(js, new Nav(), session, privacy);
        using var response = Excel(); var memory = new MemoryStream(Encoding.UTF8.GetBytes("workbook"));
        await using var file = new DownloadedFile(response, memory, ExcelRankingWriter.ContentType, "Kết quả.xlsx", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.OpenAsync(file));
        await files.SaveRankingAsync(file);
        Assert.Equal("workbook", js.Data); Assert.Equal("Kết quả.xlsx", js.Name); Assert.False(js.Preview);
        Assert.True(memory.CanRead); // caller owns it through the complete JS transfer
        await session.SignOutAsync(); await Assert.ThrowsAsync<InvalidOperationException>(() => files.SaveRankingAsync(file));
    }

    private static HttpResponseMessage Excel()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("PK-data")) };
        response.Content.Headers.ContentType = new(ExcelRankingWriter.ContentType);
        response.Content.Headers.ContentDisposition = new("attachment") { FileNameStar = "Xếp hạng lượt 31.xlsx" };
        return response;
    }
    private sealed class Nav : NavigationManager { public Nav() => Initialize("https://test.invalid/", "https://test.invalid/results"); }
    private sealed class StreamJs : IJSRuntime, IJSObjectReference
    {
        public string? Data, Name; public bool Preview;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
        {
            if (identifier == "import") return (TValue)(object)this;
            if (identifier == "open")
            {
                Assert.IsType<DotNetStreamReference>(args![0]); var stream = (DotNetStreamReference)args[0]!;
                using var reader = new StreamReader(stream.Stream, leaveOpen: true); Data = await reader.ReadToEndAsync(ct);
                Name = (string)args[2]!; Preview = (bool)args[3]!; return (TValue)(object)false;
            }
            return default!;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
