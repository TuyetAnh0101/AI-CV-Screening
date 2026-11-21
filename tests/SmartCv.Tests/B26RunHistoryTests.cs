extern alias web;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SmartCv.Core.Contracts;
using SmartCv.Core.Privacy;
using SmartCv.Infrastructure.Data;
using SmartCv.Tests.Runs;
using web::SmartCv.Web.Components.Layout;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;
// "Runs" alone would resolve to the SmartCv.Tests.Runs namespace inside SmartCv.Tests.
using RunsPage = web::SmartCv.Web.Components.Pages.Runs;
using RunDetailPage = web::SmartCv.Web.Components.Pages.RunDetail;

namespace SmartCv.Tests;

#pragma warning disable BL0006 // Dispatch real Blazor component events without another package (same approach as B19/B20).
public class B26RunHistoryTests
{
    // ---------------- URL filter ----------------

    [Theory]
    [InlineData(null, null, null, null, null, true)]
    [InlineData("Screening", null, null, null, null, true)]
    [InlineData("promptTest", "completed", null, null, null, true)]
    [InlineData("Other", null, null, null, null, false)]
    [InlineData(null, "Running", null, null, null, true)]
    [InlineData(null, "Done", null, null, null, false)]
    [InlineData(null, null, "2026-11-01", "2026-11-17", null, true)]
    [InlineData(null, null, "2026-11-17", "2026-11-17", null, true)]
    [InlineData(null, null, "2024-02-29", null, null, true)]
    [InlineData(null, null, "2026-02-29", null, null, false)]
    [InlineData(null, null, "2026-1-01", null, null, false)]
    [InlineData(null, null, "17/11/2026", null, null, false)]
    [InlineData(null, null, "2026-11-18", "2026-11-17", null, false)]
    [InlineData(null, null, "0001-01-01", null, null, false)]
    [InlineData(null, null, null, "9999-12-31", null, false)]
    [InlineData(null, null, null, null, "2", true)]
    [InlineData(null, null, null, null, "107374183", true)]   // (page − 1) × 20 = 2 147 483 640 ≤ int.MaxValue
    [InlineData(null, null, null, null, "107374184", false)]  // skip would overflow Int32 (A25 rule)
    [InlineData(null, null, null, null, "0", false)]
    [InlineData(null, null, null, null, "-1", false)]
    [InlineData(null, null, null, null, "abc", false)]
    [InlineData(null, null, null, null, "1.5", false)]
    [InlineData(null, null, null, null, " 2", false)]
    [InlineData(null, null, null, null, "2147483648", false)]
    public void Filter_is_strict_and_invalid_input_never_becomes_all_runs(string? type, string? status, string? from, string? to, string? page, bool valid)
    {
        var (filter, error) = RunHistoryFilter.Parse(type, status, from, to, page);
        Assert.Equal(valid, filter is not null);
        Assert.Equal(!valid, error is not null);
    }

    [Fact]
    public void Filter_URL_and_API_query_are_canonical_and_always_paged()
    {
        var filter = new RunHistoryFilter("PromptTest", "Completed", new(2026, 11, 1), new(2026, 11, 17), 2);
        Assert.Equal("/runs?type=PromptTest&status=Completed&from=2026-11-01&to=2026-11-17&page=2", filter.Url);
        Assert.Equal("page=2&pageSize=20&type=PromptTest&status=Completed&from=2026-11-01&to=2026-11-17", filter.ApiQuery);
        Assert.Equal(filter, RunHistoryFilter.FromUri("https://app.test" + filter.Url).Filter);
        Assert.Equal("/runs", new RunHistoryFilter().Url);
        Assert.Equal("page=1&pageSize=20", new RunHistoryFilter().ApiQuery);
        Assert.Equal("/runs?type=Screening&status=Running", RunHistoryFilter.Parse("screening", "running", "", "", "1").Filter!.Url);
        Assert.Equal(new RunHistoryFilter(Status: "Running"), RunHistoryFilter.FromUri("https://app.test/runs?status=Running").Filter); // B19 KPI link
        Assert.Equal(new RunHistoryFilter(), RunHistoryFilter.FromUri("https://app.test/runs?utm=x&jobId=3").Filter);           // unrelated keys ignored
    }

    [Theory]
    [InlineData("?type=Screening&type=PromptTest")]
    [InlineData("?status=Queued&status=Running")]
    [InlineData("?from=2026-11-01&from=2026-11-02")]
    [InlineData("?page=1&page=2")]
    [InlineData("?to=2026-11-17&to=")]
    public void Duplicate_URL_filters_are_rejected(string query) =>
        Assert.Null(RunHistoryFilter.FromUri("https://app.test/runs" + query).Filter);

    // ---------------- Display text ----------------

    [Theory]
    [InlineData(null, "—")]
    [InlineData(-1L, "—")]
    [InlineData(0L, "Dưới 1 giây")]
    [InlineData(45L, "45 giây")]
    [InlineData(60L, "1 phút")]
    [InlineData(200L, "3 phút 20 giây")]
    [InlineData(3600L, "1 giờ")]
    [InlineData(3900L, "1 giờ 5 phút")]
    [InlineData(86400L, "1 ngày")]
    [InlineData(90000L, "1 ngày 1 giờ")]
    [InlineData(2147483647L, "24.855 ngày 3 giờ")]
    public void Duration_distinguishes_unknown_zero_and_very_long(long? seconds, string expected) =>
        Assert.Equal(expected, RunText.Duration(seconds));

    [Fact]
    public void Run_text_keeps_null_values_distinct_and_never_shows_active_runs_as_finished()
    {
        Assert.Equal("Đang chạy", RunText.RunDuration("Running", 30));
        Assert.Equal("Chưa bắt đầu chấm", RunText.RunDuration("Queued", null));
        Assert.Equal("Chưa ghi nhận", RunText.RunDuration("Completed", null));
        Assert.Equal("Dưới 1 giây", RunText.RunDuration("Completed", 0));
        Assert.Equal("—", RunText.Accuracy(null)); Assert.Equal("0%", RunText.Accuracy(0)); Assert.Equal("66,7%", RunText.Accuracy(66.67m));
        Assert.Equal("30% từ khóa · 70% AI", RunText.Weights(.3m, .7m)); Assert.Equal("33,3% từ khóa · 66,7% AI", RunText.Weights(.333m, .667m));
        Assert.Equal("30", RunText.KeywordShare(.3m, .7m)); Assert.Null(RunText.KeywordShare(0, 0)); Assert.Null(RunText.KeywordShare(-1, 2));
        Assert.Equal("0,2", RunText.Temperature(.2m)); Assert.Equal("1,0", RunText.Temperature(1)); Assert.Equal("0,35", RunText.Temperature(.35m));
        Assert.Equal("70 điểm", RunText.Threshold(70)); Assert.Equal("Trang 2/5 · 87 lượt", RunText.PageInfo(2, 5, 87));
        Assert.Equal("Trang 1/1 · 1.234 lượt", RunText.PageInfo(1, 1, 1234)); Assert.Equal("Trang 1/1 · 0 lượt", RunText.PageInfo(1, 0, 0));
        Assert.Equal(("Không chấm được", "AI lỗi · chỉ có điểm từ khóa"), (RunText.FailureKind("Failed"), RunText.FailureKind("KeywordOnly")));
        Assert.Equal(("Sàng lọc", "Đánh giá prompt"), (RunText.Type("Screening"), RunText.Type("PromptTest")));
        Assert.Equal(12, RunText.DurationSeconds(new(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 17, 0, 0, 12, DateTimeKind.Utc)));
        Assert.Null(RunText.DurationSeconds(new(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc), null));
    }

    [Fact]
    public void Times_use_Vietnam_calendar_days_and_unrepresentable_values_are_safe()
    {
        Assert.Equal(("17/11/2026", "23:59"), (RunText.VnDate(new DateTime(2026, 11, 17, 16, 59, 59, DateTimeKind.Utc)), RunText.VnClock(new DateTime(2026, 11, 17, 16, 59, 59, DateTimeKind.Utc))));
        Assert.Equal(("18/11/2026", "00:00"), (RunText.VnDate(new DateTime(2026, 11, 17, 17, 0, 0, DateTimeKind.Utc)), RunText.VnClock(new DateTime(2026, 11, 17, 17, 0, 0, DateTimeKind.Utc))));
        Assert.Equal("18/11/2026", RunText.VnDate(new DateTime(2026, 11, 17, 17, 0, 0, DateTimeKind.Unspecified)));
        Assert.Equal("—", RunText.VnDate(DateTime.MaxValue)); Assert.Equal("—", RunText.VnDateTime(default(DateTime))); Assert.Null(RunText.IsoTime(null));
        Assert.Equal("2026-11-18T00:00:00+07:00", RunText.IsoTime(new DateTime(2026, 11, 17, 17, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Failure_reasons_only_pass_A25_controlled_sentences()
    {
        const string known = "Chỉ tính điểm từ khóa. Dịch vụ AI đang quá tải.";
        Assert.Equal(known, RunText.SafeReason("KeywordOnly", known));
        Assert.Equal("AI chưa chấm thành công; chỉ tính điểm từ khóa.", RunText.SafeReason("KeywordOnly", "HTTP 429 https://x.googleapis.com/?key=AIzaSECRET"));
        Assert.Equal("Không chấm được hồ sơ. Vui lòng kiểm tra trạng thái CV và cấu hình chấm.", RunText.SafeReason("Failed", "C:\\Users\\hr\\cv.pdf Nguyễn Văn A"));
        Assert.Equal("Không chấm được hồ sơ. Vui lòng kiểm tra trạng thái CV và cấu hình chấm.", RunText.SafeReason("Failed", null));
        Assert.Contains("tải lại tệp rõ nét hơn", RunText.CvHint("Failed")); Assert.NotNull(RunText.CvHint("Pending")); Assert.Null(RunText.CvHint("Completed"));
    }

    // ---------------- API client contract ----------------

    [Fact]
    public async Task B7_list_call_still_omits_page_while_history_always_sends_page_and_pageSize()
    {
        var api = new Api().On("/api/runs", (request, _) => Task.FromResult(request.RequestUri!.Query.Contains("page=")
            ? Json(Page(1, 0)) : Json(new List<RunSummaryDto> { new() { RunId = 9, RunType = "Screening" } })))
            .On("/api/runs/12/failures", new RunFailuresDto { RunId = 12 });
        var client = Client(api);
        var list = await client.ListScreeningAsync(); Assert.True(list.IsSuccess); Assert.Equal(9, Assert.Single(list.Data!).RunId);
        Assert.True((await client.ListPageAsync(new())).IsSuccess);
        Assert.True((await client.GetFailuresAsync(12)).IsSuccess);
        Assert.Equal(new[] { "/api/runs?type=Screening&take=50", "/api/runs?page=1&pageSize=20", "/api/runs/12/failures" }, api.Calls);
    }

    [Fact]
    public async Task Paged_client_reads_real_A25_with_inclusive_Vietnam_days_and_legacy_list_is_unchanged()
    {
        using var factory = RunHistoryEndpointTests.Factory(); var seed = await SeedAsync(factory);
        var log = new Recorder(factory.Server.CreateHandler());
        var client = new RunApiClient(new HttpClient(log) { BaseAddress = new Uri("http://localhost/") }, NullLogger<RunApiClient>.Instance);
        // Seed: runs at 23:59:59 (16th), 00:00:00 ×2 and 23:59:59 (17th), 00:00 (18th), 19th — Vietnam time.
        var day = await client.ListPageAsync(new(From: new(2026, 11, 17), To: new(2026, 11, 17)));
        Assert.Equal(new[] { seed.Runs[3], seed.Runs[2], seed.Runs[1] }, day.Data!.Items.Select(r => r.RunId));
        Assert.Equal(seed.Runs[4], Assert.Single((await client.ListPageAsync(new(From: new(2026, 11, 18), To: new(2026, 11, 18)))).Data!.Items).RunId);
        Assert.Equal(75m, Assert.Single((await client.ListPageAsync(new("PromptTest", "Completed"))).Data!.Items).AccuracyPercent);
        var paged = (await client.ListPageAsync(new(Page: 2))).Data!; Assert.Equal((2, 20, 6, 1), (paged.Page, paged.PageSize, paged.TotalCount, paged.TotalPages)); Assert.Empty(paged.Items);
        var legacy = await client.ListScreeningAsync(3); Assert.True(legacy.IsSuccess); Assert.Equal(3, legacy.Data!.Count);
        Assert.Contains("/api/runs?type=Screening&take=3", log.Calls);
        Assert.All(log.Calls.Where(c => c.Contains("page=")), c => Assert.Contains("pageSize=20", c));
    }

    // ---------------- /runs ----------------

    [Fact]
    public async Task History_page_renders_real_A25_rows_and_links_keep_the_filter()
    {
        using var factory = RunHistoryEndpointTests.Factory(); var seed = await SeedAsync(factory);
        var log = new Recorder(factory.Server.CreateHandler());
        using var sp = Services(log, new Nav("https://app.test/runs?from=2026-11-17&to=2026-11-17"));
        var html = Decode(await Html<RunsPage>(sp));
        Assert.Contains("/api/runs?page=1&pageSize=20&from=2026-11-17&to=2026-11-17", log.Calls);
        foreach (var id in new[] { seed.Runs[1], seed.Runs[2], seed.Runs[3] }) Assert.Contains($"href=\"/runs/{id}?from=2026-11-17&to=2026-11-17\"", html);
        Assert.DoesNotContain($"/runs/{seed.Runs[0]}?", html); Assert.DoesNotContain($"/runs/{seed.Runs[4]}?", html);
        Assert.Contains("Trang 1/1 · 3 lượt", html); Assert.Contains("75%", html); Assert.Contains("A25 historical set", html);
        Assert.Contains("A25 old", html); Assert.Contains("saved-model-A25", html); Assert.Matches("Temperature <b[^>]*>0,42</b>", html);
        Assert.Contains("Thất bại", html); Assert.Contains("12 giây", html); Assert.Matches("class=\"failed-count\"[^>]*>2<", html);
        Assert.Contains("Đang lọc: 17/11/2026 – 17/11/2026", html);
        Assert.DoesNotContain(RunHistoryEndpointTests.Secret, html); Assert.DoesNotContain("Synthetic Candidate", html);
        Assert.All(log.Methods, m => Assert.Equal("GET", m));
    }

    [Fact]
    public async Task Table_shows_every_business_column_with_null_zero_and_huge_values_kept_distinct()
    {
        var longPrompt = "v12-prompt-rat-dai-" + new string('x', 120);
        var api = new Api().On("/api/runs", HistoryRoute(_ => Page(2, 87,
            Item(41, "PromptTest", accuracy: null, duration: 0, prompt: longPrompt),
            Item(42, status: "Running", duration: null),
            Item(43, failed: 3, duration: int.MaxValue, jobs: [Job(1, "Backend Developer"), Job(2, "QA Engineer"), Job(3, "Data Analyst")]),
            Item(44, "PromptTest", accuracy: 0m),
            Item(45, duration: null))));
        using var sp = Services(api, new Nav("https://app.test/runs?page=2"));
        var raw = await Html<RunsPage>(sp); var html = Decode(raw);
        Assert.Contains("Trang 2/5 · 87 lượt", html); Assert.Contains("Hiển thị 21–25", html);
        Assert.DoesNotMatch("id=\"runs-prev\"[^>]*disabled", raw); Assert.DoesNotMatch("id=\"runs-next\"[^>]*disabled", raw);
        Assert.Contains("Dưới 1 giây", html); Assert.Contains("Đang chạy", html); Assert.Contains("12 đã xử lý", html);
        Assert.Contains("24.855 ngày 3 giờ", html); Assert.Contains("Chưa ghi nhận", html);
        // Scoped CSS adds a b-xxxx attribute to each element; the Screening "—" also carries aria-hidden.
        Assert.Single(Regex.Matches(html, "class=\"accuracy is-empty\" b-\\w+>—</span><small[^>]*>chưa có</small>"));
        Assert.Equal(3, Regex.Matches(html, "class=\"accuracy is-empty\" aria-hidden=\"true\"[^>]*>—</span>").Count);
        Assert.Single(Regex.Matches(html, "class=\"accuracy ?\" b-\\w+>0%</span>"));
        Assert.Matches("class=\"failed-count\"[^>]*>3<", html); Assert.Contains(longPrompt, html);
        Assert.Contains("+1 vị trí khác: Data Analyst", html); Assert.Contains("Bộ CV có nhãn", html); Assert.Matches("Temperature <b[^>]*>0,2</b>", html);
        Assert.Contains("href=\"/runs/41?page=2\"", html); Assert.Contains("Đánh giá prompt", html); Assert.Contains("Sàng lọc", html);
        Assert.Contains("<caption", raw); Assert.Contains("scope=\"row\"", raw); Assert.Equal(8, Regex.Matches(raw, "<th scope=\"col\"").Count);
        Assert.Contains("Có lượt đang chờ hoặc đang phân tích", html); Assert.Equal(new[] { "/api/runs?page=2&pageSize=20" }, api.Calls);
    }

    [Theory]
    [InlineData("?type=Other")]
    [InlineData("?status=Done")]
    [InlineData("?from=2026-02-30")]
    [InlineData("?from=2026-12-02&to=2026-12-01")]
    [InlineData("?from=0001-01-01")]
    [InlineData("?page=0")]
    [InlineData("?page=-1")]
    [InlineData("?page=abc")]
    [InlineData("?page=107374184")]
    [InlineData("?type=Screening&type=PromptTest")]
    public async Task Invalid_deep_links_explain_the_problem_and_send_no_API_request(string query)
    {
        var api = new Api().On("/api/runs", HistoryRoute(_ => Page(1, 0)));
        using var sp = Services(api, new Nav("https://app.test/runs" + query));
        var html = Decode(await Html<RunsPage>(sp));
        Assert.Contains("Chưa áp dụng bộ lọc này", html); Assert.Contains("Bộ lọc chưa hợp lệ", html); Assert.Contains("id=\"runs-invalid-clear\"", html);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task System_empty_filtered_empty_and_page_beyond_the_end_are_distinct_states()
    {
        var api = new Api().On("/api/runs", HistoryRoute(f => f.Page == 9 ? Page(9, 87) : Page(f.Page, 0)));
        using (var sp = Services(api, new Nav()))
        {
            var html = Decode(await Html<RunsPage>(sp));
            Assert.Contains("Chưa có lượt chấm nào", html); Assert.Contains("Tải CV lên, gán vào vị trí tuyển dụng", html); Assert.DoesNotContain("runs-empty-clear", html);
        }
        using (var sp = Services(api, new Nav("https://app.test/runs?status=Failed")))
        {
            var html = Decode(await Html<RunsPage>(sp));
            Assert.Contains("Không có lượt chấm khớp bộ lọc", html); Assert.Contains("id=\"runs-empty-clear\"", html); Assert.DoesNotContain("Chưa có lượt chấm nào", html);
        }
        using (var sp = Services(api, new Nav("https://app.test/runs?page=9")))
        {
            var html = Decode(await Html<RunsPage>(sp));
            Assert.Contains("Trang 9 không có lượt nào", html); Assert.Contains("Về trang 5", html);
        }
    }

    [Theory]
    [InlineData(400, "Máy chủ không chấp nhận bộ lọc này")]
    [InlineData(404, "Không tìm thấy API lịch sử lượt chấm")]
    [InlineData(500, "Máy chủ API gặp lỗi khi tải lịch sử lượt chấm")]
    [InlineData(503, "Máy chủ API tạm thời không sẵn sàng")]
    [InlineData(0, "Không kết nối được máy chủ API")]
    [InlineData(-1, "Máy chủ API phản hồi quá lâu")]
    public async Task History_API_failures_show_safe_messages_and_retry_recovers(int code, string expected)
    {
        var broken = true;
        var api = new Api().On("/api/runs", async (_, _) =>
        {
            await Task.Yield();
            if (!broken) return Json(Page(1, 1, Item(7)));
            if (code == 0) throw new HttpRequestException("PRIVATE socket 10.0.0.5");
            if (code == -1) throw new TaskCanceledException("PRIVATE timeout");
            return Json(new { title = "PRIVATE SqlException", detail = "PRIVATE C:\\db.mdf" }, code);
        });
        using var sp = Services(api, new Nav()); await using var ui = new Ui(sp); await ui.Show<RunsPage>();
        var text = await ui.Until(t => t.Contains(expected));
        Assert.Contains("Chưa tải được lịch sử lượt chấm", text); Assert.DoesNotContain("PRIVATE", text); Assert.True(await ui.Has("runs-retry"));
        broken = false; await ui.Click("runs-retry");
        await ui.Until(t => t.Contains("Trang 1/1 · 1 lượt"));
    }

    [Fact]
    public async Task Workspace_rejects_a_page_or_type_the_API_did_not_answer_for()
    {
        var api = new Api().On("/api/runs", HistoryRoute(f => f.Page == 1 ? Page(3, 87, Item(1)) : Page(f.Page, 87, Item(2, "PromptTest"))));
        using var state = new RunHistoryWorkspace(Client(api));
        await state.LoadAsync(new()); Assert.False(state.ShowData); Assert.Contains("không khớp", state.Error);
        await state.LoadAsync(new(Type: "Screening", Page: 2)); Assert.False(state.ShowData); Assert.Contains("không khớp", state.Error);
    }

    [Fact]
    public async Task Fast_filter_change_cancels_the_old_request_and_a_late_old_response_never_replaces_the_new_one()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new Api().On("/api/runs", async (request, ct) =>
        {
            if (request.RequestUri!.Query.Contains("type=Screening"))
            {
                using var registration = ct.Register(() => canceled.TrySetResult());
                started.TrySetResult();
                await gate.Task; // ignores cancellation on purpose: the late body must still be dropped
                return Json(Page(1, 1, Item(111, prompt: "OLD-SCREENING-RESPONSE")));
            }
            return Json(Page(1, 1, Item(222, "PromptTest", accuracy: 80, prompt: "NEW-PROMPTTEST-RESPONSE")));
        });
        var nav = new Nav("https://app.test/runs?type=Screening");
        using var sp = Services(api, nav); await using var ui = new Ui(sp);
        var showing = ui.Show<RunsPage>();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await ui.Go(nav, "/runs?type=PromptTest");
        await ui.Until(t => t.Contains("NEW-PROMPTTEST-RESPONSE"));
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        gate.SetResult(); await showing.WaitAsync(TimeSpan.FromSeconds(10)); await Task.Delay(50);
        var text = await ui.Everything();
        Assert.Contains("NEW-PROMPTTEST-RESPONSE", text); Assert.DoesNotContain("OLD-SCREENING-RESPONSE", text);
        Assert.Equal("PromptTest", await ui.Attribute("runs-type", "value"));
    }

    [Fact]
    public async Task Filters_and_pages_live_in_the_URL_and_back_forward_resynchronise_without_duplicate_requests()
    {
        var api = new Api().On("/api/runs", HistoryRoute(f => f.Type == "PromptTest"
            ? Page(f.Page, 3, Item(301, "PromptTest", accuracy: 66.7m))
            : Page(f.Page, 87, Enumerable.Range(0, f.Page == 5 ? 7 : 20).Select(i => Item(1000 - (f.Page - 1) * 20 - i)).ToArray())));
        var nav = new Nav(); using var sp = Services(api, nav); await using var ui = new Ui(sp); await ui.Show<RunsPage>();
        await ui.Until(t => t.Contains("Trang 1/5 · 87 lượt"));
        Assert.Equal("True", await ui.Attribute("runs-prev", "disabled")); Assert.Null(await ui.Attribute("runs-next", "disabled"));
        await ui.Click("runs-next"); await ui.Until(t => t.Contains("Trang 2/5 · 87 lượt"));
        Assert.Equal("https://app.test/runs?page=2", nav.Uri);
        await ui.Change("runs-type", "PromptTest"); await ui.Until(t => t.Contains("Trang 1/1 · 3 lượt"));
        Assert.Equal("https://app.test/runs?type=PromptTest", nav.Uri); // a changed filter goes back to page 1
        await ui.Go(nav, "/runs?page=2"); await ui.Until(t => t.Contains("Trang 2/5"));             // Back
        Assert.Equal("", await ui.Attribute("runs-type", "value"));
        await ui.SetParameters(new());                                                                 // router re-render: no extra request
        await ui.Go(nav, "/runs?type=PromptTest"); await ui.Until(t => t.Contains("Trang 1/1 · 3 lượt")); // Forward
        Assert.Equal("PromptTest", await ui.Attribute("runs-type", "value"));
        Assert.Equal(new[] { "/api/runs?page=1&pageSize=20", "/api/runs?page=2&pageSize=20", "/api/runs?page=1&pageSize=20&type=PromptTest",
            "/api/runs?page=2&pageSize=20", "/api/runs?page=1&pageSize=20&type=PromptTest" }, api.Calls);
        await ui.Go(nav, "/runs?page=5"); await ui.Until(t => t.Contains("Trang 5/5"));
        Assert.Equal("True", await ui.Attribute("runs-next", "disabled")); Assert.Null(await ui.Attribute("runs-prev", "disabled"));
        await ui.Click("runs-clear"); await ui.Until(t => t.Contains("Trang 1/5")); Assert.Equal("https://app.test/runs", nav.Uri);
    }

    [Fact]
    public async Task Invalid_date_range_is_explained_and_never_navigates_or_requests()
    {
        var api = new Api().On("/api/runs", HistoryRoute(f => Page(f.Page, 1, Item(5))));
        var nav = new Nav(); using var sp = Services(api, nav); await using var ui = new Ui(sp); await ui.Show<RunsPage>();
        await ui.Until(t => t.Contains("Trang 1/1"));
        await ui.Change("runs-from", "2026-11-18");
        await ui.Until(t => t.Contains("Trang 1/1") && nav.Uri == "https://app.test/runs?from=2026-11-18");
        var calls = api.Calls.Count;
        await ui.Change("runs-to", "2026-11-17");
        var text = await ui.Until(t => t.Contains("Từ ngày phải trước hoặc bằng Đến ngày."));
        Assert.Contains("Chưa áp dụng bộ lọc này", text); Assert.DoesNotContain("Trang 1/1", text);
        Assert.Equal("https://app.test/runs?from=2026-11-18", nav.Uri); Assert.Equal(calls, api.Calls.Count);
        Assert.Equal("true", await ui.Attribute("runs-to", "aria-invalid")); Assert.Equal("false", await ui.Attribute("runs-type", "aria-invalid"));
        await ui.Change("runs-to", "2026-11-18");
        await ui.Until(t => t.Contains("Trang 1/1") && nav.Uri == "https://app.test/runs?from=2026-11-18&to=2026-11-18");
        Assert.Contains("/api/runs?page=1&pageSize=20&from=2026-11-18&to=2026-11-18", api.Calls);
    }

    // ---------------- /runs/{id} ----------------

    [Fact]
    public async Task Detail_shows_the_saved_snapshot_and_separates_failures_without_exposing_private_data()
    {
        using var factory = RunHistoryEndpointTests.Factory(); var seed = await SeedAsync(factory);
        var log = new Recorder(factory.Server.CreateHandler());
        using var sp = Services(log, new Nav($"https://app.test/runs/{seed.MixedRun}"));
        var raw = await Html<RunDetailPage>(sp, new() { ["Id"] = seed.MixedRun }); var html = Decode(raw);
        Assert.Contains($"Chi tiết lượt chấm #{seed.MixedRun}", html);
        Assert.Contains("A25 old", html); Assert.Contains($"Mã phiên bản #{seed.OldPrompt}", html); Assert.Contains("saved-model-A25", html);
        Assert.Contains(">0,42<", html); Assert.Contains("40% từ khóa · 60% AI", html); Assert.Contains("81 điểm", html);
        Assert.Contains("style=\"width:40%\"", html);
        Assert.DoesNotContain("A25 OLD SAVED SYSTEM PROMPT", html); Assert.DoesNotContain("A25 new", html);   // prompt collapsed; never the active version
        Assert.Equal(new[] { $"/api/runs/{seed.MixedRun}", $"/api/runs/{seed.MixedRun}/failures" }, log.Calls);
        Assert.Contains($"href=\"/results?runId={seed.MixedRun}&jobId=1\"", html); Assert.Contains($"href=\"/results?runId={seed.MixedRun}&jobId=2\"", html);
        Assert.Contains("4 hồ sơ · 2 lỗi", html);
        Assert.Contains("Không đọc được nội dung CV. Hãy kiểm tra và tải lại tệp rõ hơn.", html);
        Assert.Contains("Chỉ tính điểm từ khóa. Dịch vụ AI đã hết hạn mức hoặc đang giới hạn tần suất.", html);
        Assert.Contains("Thông tin lỗi đã được ẩn để bảo vệ hồ sơ đã ẩn danh.", html);
        Assert.Single(Regex.Matches(html, "CV không đọc được chữ"));
        Assert.Equal(2, Regex.Matches(raw, "<tr class=\"is-failed\"").Count); Assert.Single(Regex.Matches(raw, "<tr class=\"is-keyword\""));
        Assert.Matches("<b[^>]*>2</b> Không chấm được", html); Assert.Matches("<b[^>]*>1</b> AI lỗi · chỉ có điểm từ khóa", html);
        Assert.Contains("CV đã ẩn tên", html); Assert.Contains("Ứng viên #", html);
        foreach (var hidden in new[] { "Synthetic Candidate", "synthetic-a25-", RunHistoryEndpointTests.Secret, RunHistoryEndpointTests.Erased, "C:\\private", "SYNTHETIC_TEXT" })
            Assert.DoesNotContain(hidden, html);

        var anonymity = new AnonymityState(); await anonymity.SetAsync(false);
        using var open = Services(new Recorder(factory.Server.CreateHandler()), new Nav($"https://app.test/runs/{seed.MixedRun}"), anonymity);
        var visible = Decode(await Html<RunDetailPage>(open, new() { ["Id"] = seed.MixedRun }));
        Assert.Contains("Synthetic Candidate 1-0", visible); Assert.Contains("synthetic-a25-1-1.pdf", visible);
        Assert.Contains(RetentionRules.AnonymizedFileName, visible); Assert.Contains("Chưa rõ tên ứng viên", visible);
        Assert.DoesNotContain(RunHistoryEndpointTests.Secret, visible); Assert.DoesNotContain(RunHistoryEndpointTests.Erased, visible); Assert.DoesNotContain("C:\\private", visible);
    }

    [Fact]
    public async Task Saved_prompt_of_the_run_is_hidden_until_requested_and_rendered_as_plain_text()
    {
        using var factory = RunHistoryEndpointTests.Factory(); var seed = await SeedAsync(factory);
        using (var real = Services(new Recorder(factory.Server.CreateHandler()), new Nav($"https://app.test/runs/{seed.Runs[3]}")))
        {
            await using var ui = new Ui(real); await ui.Show<RunDetailPage>(new() { ["Id"] = seed.Runs[3] });
            await ui.Until(t => t.Contains("Xem nội dung Prompt")); await ui.Click("prompt-toggle");
            var text = await ui.Until(t => t.Contains("A25 OLD SAVED SYSTEM PROMPT"));
            Assert.DoesNotContain("A25 NEW ACTIVE SYSTEM PROMPT", text); Assert.Contains("A25 historical set", text);
        }

        const string prompt = "SAVED PROMPT v1 <script>alert('x')</script>\n<b>không in đậm</b>";
        var api = new Api().On("/api/runs/12", Progress(12, prompt: prompt)).On("/api/runs/12/failures", Failures(12));
        using var sp = Services(api, new Nav("https://app.test/runs/12")); await using var page = new Ui(sp);
        await page.Show<RunDetailPage>(new() { ["Id"] = 12 });
        var before = await page.Until(t => t.Contains("Xem nội dung Prompt"));
        Assert.DoesNotContain("SAVED PROMPT v1", before); Assert.Equal("false", await page.Attribute("prompt-toggle", "aria-expanded"));
        Assert.Contains($"{prompt.Length} ký tự · 2 dòng", before);
        await page.Click("prompt-toggle"); await page.Until(t => t.Contains("Ẩn nội dung Prompt"));
        Assert.Equal("true", await page.Attribute("prompt-toggle", "aria-expanded"));
        Assert.Contains(prompt, await page.TextNodes());                 // a text node: Blazor encodes it, never parsed as HTML
        Assert.DoesNotContain("alert(", await page.MarkupNodes());
        await page.Click("prompt-toggle"); Assert.DoesNotContain("SAVED PROMPT v1", await page.Until(t => t.Contains("Xem nội dung Prompt")));
    }

    [Fact]
    public async Task Result_links_target_the_viewed_run_and_the_PromptTest_link_only_reads_existing_results()
    {
        var api = new Api().On("/api/runs/12", Progress(12)).On("/api/runs/12/failures", Failures(12))
            .On("/api/runs/40", Progress(40, "PromptTest", accuracy: 80m)).On("/api/runs/40/failures", new RunFailuresDto { RunId = 40 });
        using var screeningSp = Services(api, new Nav("https://app.test/runs/12?type=Screening&page=2"));
        var screening = Decode(await Html<RunDetailPage>(screeningSp, new() { ["Id"] = 12 }));
        Assert.Contains("href=\"/results?runId=12&jobId=1\"", screening); Assert.Contains("href=\"/results?runId=12&jobId=2\"", screening);
        Assert.Contains("href=\"/runs?type=Screening&page=2\"", screening);          // back to the same history page
        Assert.Contains("href=\"/results/evaluations/501?runId=12&jobId=1\"", screening);
        Assert.Contains("2 hồ sơ · 1 lỗi", screening); Assert.DoesNotContain("Tỷ lệ đúng", screening);
        using var promptSp = Services(api, new Nav("https://app.test/runs/40?page=0"));
        var prompt = Decode(await Html<RunDetailPage>(promptSp, new() { ["Id"] = 40 }));
        Assert.Contains("href=\"/prompts?testRunId=40\"", prompt); Assert.Contains("Xem đánh giá Prompt", prompt); Assert.Contains("80%", prompt);
        Assert.Contains("Bộ CV có nhãn", prompt); Assert.Contains("href=\"/runs\"", prompt); Assert.DoesNotContain("/results?runId=40", prompt);
        Assert.Contains("Không có hồ sơ lỗi trong lượt này.", prompt);
        Assert.All(api.Methods, m => Assert.Equal("GET", m));
        Assert.DoesNotContain(api.Calls, c => c.Contains("prompt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Anonymity_hides_names_files_and_raw_errors_everywhere_and_follows_live_changes()
    {
        var api = new Api().On("/api/runs/12", Progress(12)).On("/api/runs/12/failures", Failures(12));
        var anonymity = new AnonymityState();
        using var sp = Services(api, new Nav("https://app.test/runs/12"), anonymity); await using var ui = new Ui(sp);
        await ui.Show<RunDetailPage>(new() { ["Id"] = 12 });
        var hidden = await ui.Until(t => t.Contains("Ứng viên #101"));
        Assert.DoesNotContain("PRIVATE", hidden);                    // text, markup and attribute values (aria-label, title)
        Assert.Contains("CV đã ẩn tên", hidden); Assert.Contains("CV không đọc được chữ", hidden);
        Assert.Contains("AI chưa chấm thành công; chỉ tính điểm từ khóa.", hidden);
        await ui.Dispatcher.InvokeAsync(() => anonymity.SetAsync(false));
        var shown = await ui.Until(t => t.Contains("PRIVATE Nguyễn Văn A"));
        Assert.Contains("PRIVATE-tran-thi-b.pdf", shown);
        foreach (var leak in new[] { "AIza", "googleapis", "C:\\Users", "PRIVATE list name", "PRIVATE-list.pdf" }) Assert.DoesNotContain(leak, shown);
        await ui.Dispatcher.InvokeAsync(() => anonymity.SetAsync(true));
        Assert.DoesNotContain("PRIVATE", await ui.Until(t => t.Contains("Ứng viên #101") && !t.Contains("PRIVATE Nguyễn")));
    }

    [Fact]
    public async Task Unknown_run_shows_not_found_with_retry_and_retry_can_recover()
    {
        var api = new Api();
        using var sp = Services(api, new Nav("https://app.test/runs/99999")); await using var ui = new Ui(sp);
        await ui.Show<RunDetailPage>(new() { ["Id"] = 99999 });
        var text = await ui.Until(t => t.Contains("Không có lượt chấm #99999."));
        Assert.True(await ui.Has("run-retry")); Assert.Contains("Về lịch sử lượt chấm", text); Assert.DoesNotContain("PRIVATE", text);
        Assert.DoesNotContain(api.Calls, c => c.EndsWith("/failures", StringComparison.Ordinal));
        api.On("/api/runs/99999", Progress(99999)).On("/api/runs/99999/failures", Failures(99999));
        await ui.Click("run-retry"); await ui.Until(t => t.Contains("v1-luu-tai-thoi-diem") && t.Contains("Ứng viên #101"));
    }

    [Theory]
    [InlineData(500, "Máy chủ API gặp lỗi khi tải lượt chấm")]
    [InlineData(503, "Máy chủ API tạm thời không sẵn sàng")]
    [InlineData(-1, "Máy chủ API phản hồi quá lâu")]
    public async Task Detail_server_errors_are_safe_and_not_reported_as_missing(int code, string expected)
    {
        var api = new Api().On("/api/runs/12", async (_, _) =>
        {
            await Task.Yield();
            if (code == -1) throw new TaskCanceledException("PRIVATE timeout");
            return Json(new { detail = "PRIVATE stack trace" }, code);
        });
        using var sp = Services(api, new Nav("https://app.test/runs/12"));
        var html = Decode(await Html<RunDetailPage>(sp, new() { ["Id"] = 12 }));
        Assert.Contains(expected, html); Assert.DoesNotContain("Không có lượt chấm #12", html); Assert.DoesNotContain("PRIVATE", html);
        Assert.Contains("id=\"run-retry\"", html);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Non_positive_route_id_is_rejected_without_calling_the_API(int id)
    {
        var api = new Api(); using var sp = Services(api, new Nav($"https://app.test/runs/{id}"));
        var html = Decode(await Html<RunDetailPage>(sp, new() { ["Id"] = id }));
        Assert.Contains("Mã lượt chấm không hợp lệ", html); Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Failures_error_keeps_the_summary_and_retries_only_the_failures_request()
    {
        var api = new Api().On("/api/runs/12", Progress(12)).On("/api/runs/12/failures", new { detail = "PRIVATE db" }, 500);
        using var sp = Services(api, new Nav("https://app.test/runs/12")); await using var ui = new Ui(sp);
        await ui.Show<RunDetailPage>(new() { ["Id"] = 12 });
        var text = await ui.Until(t => t.Contains("Chưa tải được danh sách hồ sơ lỗi."));
        Assert.Contains("v1-luu-tai-thoi-diem", text); Assert.Contains("Cấu hình đã dùng", text); Assert.Contains("30% từ khóa · 70% AI", text);
        Assert.DoesNotContain("PRIVATE", text); Assert.True(await ui.Has("failures-retry"));
        api.On("/api/runs/12/failures", Failures(12));
        await ui.Click("failures-retry"); await ui.Until(t => t.Contains("Ứng viên #101") && t.Contains("Ứng viên #102"));
        Assert.Equal(1, api.Calls.Count(c => c == "/api/runs/12")); Assert.Equal(2, api.Calls.Count(c => c == "/api/runs/12/failures"));
    }

    [Fact]
    public async Task Changing_the_route_id_clears_the_previous_run_before_the_next_one_arrives()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new Api().On("/api/runs/12", Progress(12, model: "MODEL-OF-RUN-12")).On("/api/runs/12/failures", Failures(12))
            .On("/api/runs/13", async (_, ct) => { await gate.Task.WaitAsync(ct); return Json(Progress(13, model: "MODEL-OF-RUN-13")); })
            .On("/api/runs/13/failures", Failures(13));
        using var sp = Services(api, new Nav("https://app.test/runs/12")); await using var ui = new Ui(sp);
        await ui.Show<RunDetailPage>(new() { ["Id"] = 12 }); await ui.Until(t => t.Contains("MODEL-OF-RUN-12") && t.Contains("Ứng viên #101"));
        var changing = ui.SetParameters(new() { ["Id"] = 13 });
        var text = await ui.Until(t => t.Contains("Chi tiết lượt chấm #13"));
        Assert.DoesNotContain("MODEL-OF-RUN-12", text); Assert.DoesNotContain("Ứng viên #101", text); Assert.Contains("Đang tải lượt chấm #13", text);
        gate.SetResult(); await changing.WaitAsync(TimeSpan.FromSeconds(10));
        await ui.Until(t => t.Contains("MODEL-OF-RUN-13"));
    }

    [Fact]
    public async Task Running_run_is_never_presented_as_finished()
    {
        var api = new Api().On("/api/runs/12", Progress(12, "PromptTest", "Running")).On("/api/runs/12/failures", new RunFailuresDto { RunId = 12 });
        using var sp = Services(api, new Nav("https://app.test/runs/12"));
        var html = Decode(await Html<RunDetailPage>(sp, new() { ["Id"] = 12 }));
        Assert.Contains("Đang phân tích", html); Assert.Contains("Chưa hoàn tất", html); Assert.Contains("Lượt đang phân tích.", html);
        Assert.Contains("Có khi lượt hoàn tất", html); Assert.Contains("Đang chạy", html); Assert.Contains("1/3", html);
        Assert.Contains("Chưa ghi nhận hồ sơ lỗi. Lượt đang chạy", html); Assert.DoesNotContain("kind-count", html); // no "0 / 0" chips beside the positive note
        Assert.DoesNotContain("Hoàn thành", html); Assert.DoesNotContain("Đã xử lý hết hồ sơ", html);
    }

    // ---------------- Navigation ----------------

    [Fact]
    public async Task Sidebar_adds_run_history_under_management_and_routes_match_B19_links()
    {
        using var sp = Services(new Api(), new Nav("https://app.test/runs/12"));
        var html = Decode(await Html<NavMenu>(sp));
        var labels = new[] { "Dashboard quản trị", "Tải hồ sơ lên", "Kết quả", "System prompt", "Tuyển dụng", "Vị trí tuyển dụng", "Lịch sử lượt chấm" };
        var positions = labels.Select(l => html.IndexOf(l, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, p => Assert.True(p >= 0)); Assert.True(positions[4] < positions[5] && positions[5] < positions[6]);
        Assert.Matches("<a[^>]*href=\"runs\"[^>]*class=\"nav-item active\"|<a[^>]*class=\"nav-item active\"[^>]*href=\"runs\"", html);
        Assert.Single(Regex.Matches(html, "nav-item active"));
        Assert.Equal("/runs", typeof(RunsPage).GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal("/runs/{Id:int}", typeof(RunDetailPage).GetCustomAttribute<RouteAttribute>()!.Template);
    }

    // ---------------- Fixtures ----------------

    private static readonly DateTime Start = new(2026, 11, 17, 1, 30, 0, DateTimeKind.Utc); // 08:30 Vietnam

    private static JobCountDto Job(int id, string title) => new() { JobId = id, Title = title, Count = 5 };

    private static RunHistoryItemDto Item(int id, string type = "Screening", string status = "Completed", decimal? accuracy = null, int failed = 0,
        int? duration = 200, string? prompt = null, List<JobCountDto>? jobs = null) => new()
    {
        RunId = id, RunType = type, Status = status, PromptVersionId = 3, PromptVersionName = prompt ?? "v2-sang-loc", ModelName = "gemini-2.5-flash",
        Temperature = .2m, KeywordWeight = .3m, AiWeight = .7m, PassThreshold = 70,
        EvaluationSetId = type == "PromptTest" ? 4 : null, EvaluationSetName = type == "PromptTest" ? "Bộ CV có nhãn" : null, AccuracyPercent = accuracy,
        TotalItems = 20, ProcessedItems = status is "Queued" or "Running" ? 12 : 20, FailedItems = failed, CreatedAt = Start,
        CompletedAt = status is "Queued" or "Running" || duration is null ? null : Start.AddSeconds(duration.Value), DurationSeconds = status is "Queued" or "Running" ? null : duration,
        Jobs = jobs ?? (type == "Screening" ? [new() { JobId = 1, Title = "Backend Developer", Count = 20 }] : [])
    };

    private static RunPageDto Page(int page, int total, params RunHistoryItemDto[] items) =>
        new() { Page = page, PageSize = 20, TotalCount = total, TotalPages = (total + 19) / 20, Items = [.. items] };

    private static RunProgressDto Progress(int id, string type = "Screening", string status = "Completed", string model = "gemini-saved",
        decimal? accuracy = null, string prompt = "SAVED PROMPT v1\nDòng 2") => new(
        id, type, status, 7, "v1-luu-tai-thoi-diem", model, .35m, .3m, .7m, 70m, 3, status is "Queued" or "Running" ? 1 : 3, 1, 33m,
        Start, status is "Queued" or "Running" ? null : Start.AddSeconds(200))
    {
        SystemPrompt = prompt, AccuracyPercent = accuracy,
        EvaluationSetId = type == "PromptTest" ? 4 : null, EvaluationSetName = type == "PromptTest" ? "Bộ CV có nhãn" : null,
        Items = [RunItem(501, 1, "Backend Developer", "Failed"), RunItem(502, 1, "Backend Developer", "Completed"), RunItem(503, 2, "QA Engineer", "Completed")]
    };

    private static RunItemDto RunItem(int evaluation, int job, string title, string status) => new()
    { EvaluationId = evaluation, ApplicationId = evaluation - 400, JobId = job, JobTitle = title, Status = status, FileName = "PRIVATE-list.pdf", CandidateName = "PRIVATE list name" };

    private static RunFailuresDto Failures(int id) => new()
    {
        RunId = id, FailedCount = 1, KeywordOnlyCount = 1,
        Items =
        [
            new() { EvaluationId = 501, ApplicationId = 101, FileName = "PRIVATE-nguyen-van-a.pdf", CandidateName = "PRIVATE Nguyễn Văn A", JobId = 1, JobTitle = "Backend Developer",
                Kind = "Failed", ErrorMessage = "Không đọc được nội dung CV. Hãy kiểm tra và tải lại tệp rõ hơn.", CvExtractionStatus = "Failed",
                CvExtractionError = "Trích xuất nội dung CV thất bại. Hãy kiểm tra và tải lại tệp rõ hơn." },
            new() { EvaluationId = 502, ApplicationId = 102, FileName = "PRIVATE-tran-thi-b.pdf", CandidateName = "PRIVATE Trần Thị B", JobId = 1, JobTitle = "Backend Developer",
                Kind = "KeywordOnly", ErrorMessage = "PRIVATE raw https://generativelanguage.googleapis.com/?key=AIzaSECRET C:\\Users\\hr\\cv.pdf", CvExtractionStatus = "Completed" }
        ]
    };

    private static async Task<RunHistoryEndpointTests.Seed> SeedAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        await db.Database.EnsureCreatedAsync();
        return await RunHistoryEndpointTests.SeedAsync(db);
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> HistoryRoute(Func<RunHistoryFilter, RunPageDto> make) => (request, _) =>
    {
        var q = QueryHelpers.ParseQuery(request.RequestUri!.Query);
        Assert.Equal("20", q.GetValueOrDefault("pageSize").ToString());
        var (filter, error) = RunHistoryFilter.Parse(q.GetValueOrDefault("type").ToString(), q.GetValueOrDefault("status").ToString(),
            q.GetValueOrDefault("from").ToString(), q.GetValueOrDefault("to").ToString(), q.GetValueOrDefault("page").ToString());
        Assert.Null(error);
        return Task.FromResult(Json(make(filter!)));
    };

    private static HttpResponseMessage Json(object value, int status = 200) =>
        new((HttpStatusCode)status) { Content = JsonContent.Create(value, value.GetType()) };

    private static RunApiClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") }, NullLogger<RunApiClient>.Instance);

    private static ServiceProvider Services(HttpMessageHandler handler, Nav nav, AnonymityState? anonymity = null)
    {
        var services = new ServiceCollection().AddLogging().AddSingleton<NavigationManager>(nav)
            .AddSingleton(new RunApiClient(new HttpClient(handler) { BaseAddress = handler is Recorder ? new Uri("http://localhost/") : new Uri("https://api.test/") }, NullLogger<RunApiClient>.Instance))
            .AddSingleton(anonymity ?? new AnonymityState()).AddSingleton<IJSRuntime>(new Js()).AddB28Auth(); // B28: menu cần người đã đăng nhập
        services.AddSupplyValueFromQueryProvider();
        return services.BuildServiceProvider();
    }

    private static async Task<string> Html<T>(ServiceProvider services, Dictionary<string, object?>? parameters = null) where T : IComponent
    {
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters ?? []))).ToHtmlString());
    }

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private sealed class Api : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> routes = new();
        public ConcurrentQueue<string> Calls { get; } = new();
        public ConcurrentQueue<string> Methods { get; } = new();
        public Api On(string path, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) { routes[path] = handler; return this; }
        public Api On(string path, object body, int status = 200) => On(path, (_, _) => Task.FromResult(Json(body, status)));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Enqueue(request.RequestUri!.PathAndQuery); Methods.Enqueue(request.Method.Method);
            return routes.TryGetValue(request.RequestUri.AbsolutePath, out var route)
                ? route(request, ct) : Task.FromResult(Json(new { detail = "PRIVATE missing fixture route" }, 404));
        }
    }

    private sealed class Recorder(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public ConcurrentQueue<string> Methods { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Enqueue(request.RequestUri!.PathAndQuery); Methods.Enqueue(request.Method.Method);
            return base.SendAsync(request, ct);
        }
    }

    private sealed class Nav : NavigationManager
    {
        public Nav(string uri = "https://app.test/runs") { Initialize("https://app.test/", uri); }
        protected override void NavigateToCore(string uri, bool forceLoad) { Uri = ToAbsoluteUri(uri).AbsoluteUri; NotifyLocationChanged(false); }
        protected override void NavigateToCore(string uri, NavigationOptions options) => NavigateToCore(uri, options.ForceLoad);
        /// <summary>Browser Back/Forward: the location changes without the page calling NavigateTo.</summary>
        public void Go(string uri) { Uri = ToAbsoluteUri(uri).AbsoluteUri; NotifyLocationChanged(false); }
    }

    private sealed class Js : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }

    /// <summary>Minimal interactive test renderer: real lifecycle, real event dispatch, inspectable render tree.</summary>
    private sealed class Ui(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        private int root = -1;
        private IComponent? component;
        protected override RendererInfo RendererInfo { get; } = new("B26-test", isInteractive: false);
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception error) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);

        public Task Show<T>(Dictionary<string, object?>? parameters = null) where T : IComponent => Dispatcher.InvokeAsync(async () =>
        {
            component = InstantiateComponent(typeof(T)); root = AssignRootComponentId(component);
            await RenderRootComponentAsync(root, ParameterView.FromDictionary(parameters ?? []));
        });

        public Task SetParameters(Dictionary<string, object?> parameters) =>
            Dispatcher.InvokeAsync(() => component!.SetParametersAsync(ParameterView.FromDictionary(parameters)));

        public Task Go(Nav nav, string uri) => Dispatcher.InvokeAsync(() => nav.Go(uri));
        public Task Click(string id) => Fire(id, "onclick", new MouseEventArgs());
        public Task Change(string id, string value) => Fire(id, "onchange", new ChangeEventArgs { Value = value });
        public Task<bool> Has(string id) => Dispatcher.InvokeAsync(() => ById(id).Any());
        public Task<string?> Attribute(string id, string name) => Dispatcher.InvokeAsync(() =>
            ById(id).Select(e => Attrs(e.F, e.I).FirstOrDefault(a => a.AttributeName == name).AttributeValue?.ToString()).FirstOrDefault());

        public Task<string> TextNodes() => Collect(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent : null);
        public Task<string> MarkupNodes() => Collect(f => f.FrameType == RenderTreeFrameType.Markup ? f.MarkupContent : null);
        /// <summary>Text, markup and every string attribute value (aria-label, title, href...).</summary>
        public Task<string> Everything() => Collect(f => f.FrameType switch
        {
            RenderTreeFrameType.Text => f.TextContent,
            RenderTreeFrameType.Markup => f.MarkupContent,
            RenderTreeFrameType.Attribute => f.AttributeValue as string,
            _ => null
        });

        public async Task<string> Until(Func<string, bool> condition, int timeoutMs = 10000)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                var text = await Everything();
                if (condition(text)) return text;
                if (watch.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met. Rendered: " + text);
                await Task.Delay(10);
            }
        }

        // Joined without separators: Razor splits "#@Id" into two adjacent text frames.
        private Task<string> Collect(Func<RenderTreeFrame, string?> select) =>
            Dispatcher.InvokeAsync(() => root < 0 ? "" : string.Concat(Trees(root).SelectMany(f => f).Select(select).Where(s => s is not null)));

        private Task Fire(string id, string name, EventArgs args) => Dispatcher.InvokeAsync(() =>
        {
            var (frames, index) = ById(id).First();
            var handler = Attrs(frames, index).Single(a => a.AttributeName == name);
            return DispatchEventAsync(handler.AttributeEventHandlerId, null, args);
        });

        private IEnumerable<(RenderTreeFrame[] F, int I)> ById(string id) => Trees(root)
            .SelectMany(f => Enumerable.Range(0, f.Length).Where(i => f[i].FrameType == RenderTreeFrameType.Element).Select(i => (F: f, I: i)))
            .Where(e => Attrs(e.F, e.I).Any(a => a.AttributeName == "id" && a.AttributeValue?.ToString() == id));

        private static IEnumerable<RenderTreeFrame> Attrs(RenderTreeFrame[] frames, int index) =>
            frames.Skip(index + 1).TakeWhile(a => a.FrameType == RenderTreeFrameType.Attribute);

        private IEnumerable<RenderTreeFrame[]> Trees(int id)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            var array = frames.Array.Take(frames.Count).ToArray();
            yield return array;
            foreach (var child in array.Where(f => f.FrameType == RenderTreeFrameType.Component))
                foreach (var nested in Trees(child.ComponentId)) yield return nested;
        }
    }
}
#pragma warning restore BL0006
