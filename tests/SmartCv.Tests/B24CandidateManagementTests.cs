extern alias web;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using SmartCv.Core.Contracts;
using SmartCv.Core.Cvs;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;
using HubPage = web::SmartCv.Web.Components.Pages.Candidates;
using DetailPage = web::SmartCv.Web.Components.Pages.CandidateDetail;
using RedirectPage = web::SmartCv.Web.Components.Pages.CvsRedirect;
using EditorDialog = web::SmartCv.Web.Components.Candidates.CandidateEditorDialog;
using MergeDialog = web::SmartCv.Web.Components.Candidates.MergeCandidateDialog;
using EraseDialog = web::SmartCv.Web.Components.Candidates.EraseDataDialog;

namespace SmartCv.Tests.B24Ui;

/// <summary>
/// B24: trang Ứng viên thống nhất (chế độ người / chế độ CV), chi tiết ứng viên, tạo / sửa / gộp, xóa dữ liệu A43,
/// bộ lọc "Chưa gắn ứng viên" ở máy chủ, chống race, không lặp lệnh ghi, che dữ liệu khi bật Ẩn danh.
/// </summary>
#pragma warning disable BL0006
public class B24CandidateManagementTests
{
    // ======================= Core + API: bộ lọc "Chưa gắn ứng viên" =======================

    [Theory]
    [InlineData("none", "none")]
    [InlineData("attached", "attached")]
    [InlineData("", null)]
    public void Cv_list_query_accepts_candidate_filter(string value, string? expected)
    {
        var (query, errors) = CvListQuery.Parse(new Dictionary<string, string[]> { ["candidate"] = [value] });
        Assert.Empty(errors);
        Assert.Equal(expected, query!.Candidate);
        Assert.Equal(expected, CvListQuery.Create(null, null, null, null, null, null, null, value).Query!.Candidate);
    }

    [Fact]
    public void Cv_list_query_rejects_unknown_or_repeated_candidate_filter()
    {
        Assert.Contains("candidate", CvListQuery.Parse(new Dictionary<string, string[]> { ["candidate"] = ["maybe"] }).Errors.Keys);
        Assert.Contains("candidate", CvListQuery.Parse(new Dictionary<string, string[]> { ["candidate"] = ["none", "attached"] }).Errors.Keys);
        Assert.StartsWith("candidate", CvListQuery.Create(null, null, null, null, null, null, null, "NONE").Error);   // mã phân biệt hoa thường như stage/status
    }

    [Fact]
    public async Task Cvs_endpoint_filters_unattached_cvs_across_the_whole_library_not_one_page()
    {
        using var factory = TestSupport.ApiFactory("Development", new() { ["CvUpload:Endpoint:Enabled"] = "true", ["Scoring:WorkerEnabled"] = "false" });
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            await db.Database.EnsureCreatedAsync();
            var person = new Candidate { FullName = "Người Có CV", Email = "co.cv@example.com", CreatedAt = DateTime.UtcNow };
            var n = 0;
            Cv Make(Candidate? owner, bool anonymized = false) => new()
            {
                CvId = Guid.NewGuid(), OriginalFileName = $"b24-{++n}.pdf", StoredPath = "synthetic", ContentType = "application/pdf",
                FileHash = n.ToString("D64"), FileSizeBytes = 1, ExtractionStatus = "Completed", UploadedAt = DateTime.UtcNow.AddMinutes(-n),
                Candidate = owner, AnonymizedAt = anonymized ? DateTime.UtcNow : null
            };
            db.AddRange(Enumerable.Range(0, 25).Select(_ => Make(null)));
            db.AddRange(Enumerable.Range(0, 3).Select(_ => Make(person)));
            db.AddRange(Enumerable.Range(0, 2).Select(_ => Make(null, anonymized: true)));   // không gắn lại được → không thuộc "chưa gắn"
            await db.SaveChangesAsync();
        }
        var none = (await client.GetFromJsonAsync<CvPageDto>("/api/cvs?candidate=none&pageSize=10"))!;
        Assert.Equal((25, 3, 10), (none.TotalCount, none.TotalPages, none.Items.Count));
        Assert.All(none.Items, cv => { Assert.Null(cv.CandidateId); Assert.False(cv.IsAnonymized); });
        var attached = (await client.GetFromJsonAsync<CvPageDto>("/api/cvs?candidate=attached"))!;
        Assert.Equal(3, attached.TotalCount);
        Assert.All(attached.Items, cv => Assert.NotNull(cv.CandidateId));
        Assert.Equal(30, (await client.GetFromJsonAsync<CvPageDto>("/api/cvs"))!.TotalCount);   // không lọc: số CV không đổi
        using var bad = await client.GetAsync("/api/cvs?candidate=unknown");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    // ======================= Helper thuần =======================

    [Fact]
    public void Candidate_list_filter_keeps_search_out_of_url_and_validates_like_A24()
    {
        var filter = new CandidateListFilter("  Nguyễn   Văn  ", 3);
        var (valid, error) = filter.Validate();
        Assert.Null(error);
        Assert.Equal("Nguyễn Văn", valid!.Search);
        Assert.Equal("/candidates?page=3", valid.Url);                        // từ khóa có thể là PII → không lên địa chỉ
        Assert.Equal("page=3&pageSize=20&search=Nguy%E1%BB%85n%20V%C4%83n", valid.ApiQuery);
        Assert.Equal("/candidates", new CandidateListFilter().Url);
        Assert.NotNull(new CandidateListFilter(new string('a', 255)).Validate().Error);
        Assert.Null(new CandidateListFilter("a").Validate().Error);           // A24 nhận 1 ký tự: không ép quy tắc 2 ký tự của kho CV
        Assert.NotNull(new CandidateListFilter(Page: 0).Validate().Error);
        Assert.Equal(2, CandidateListFilter.PageFromUri("https://app.test/candidates?page=2").Page);
        Assert.NotNull(CandidateListFilter.PageFromUri("https://app.test/candidates?page=0").Error);
        Assert.NotNull(CandidateListFilter.PageFromUri("https://app.test/candidates?page=1&page=2").Error);
        Assert.Equal(1, CandidateListFilter.PageFromUri("https://app.test/candidates").Page);
    }

    [Theory]
    [InlineData("https://app.test/candidates", "people")]
    [InlineData("https://app.test/candidates?view=cvs&search=react", "cvs")]
    [InlineData("https://app.test/candidates?view=CVS", "cvs")]
    [InlineData("https://app.test/candidates?view=unknown", "people")]
    public void Candidates_view_is_read_from_url(string uri, string expected) => Assert.Equal(expected, CandidatesView.FromUri(uri));

    [Fact]
    public void Legacy_cvs_bookmarks_keep_every_filter_and_cv_filter_round_trips_candidate()
    {
        Assert.Equal("/candidates?view=cvs", CandidatesView.LegacyCvsTarget("https://app.test/cvs"));
        Assert.Equal("/candidates?view=cvs&search=React&stage=Rejected&page=2", CandidatesView.LegacyCvsTarget("https://app.test/cvs?search=React&stage=Rejected&page=2"));
        Assert.Equal("/candidates?view=cvs&page=2", CandidatesView.LegacyCvsTarget("https://app.test/cvs?view=people&page=2"));
        var filter = new CvLibraryFilter(Candidate: "none", Page: 2);
        Assert.Equal("/candidates?view=cvs&candidate=none&page=2", filter.Url);
        Assert.Equal(filter, CvLibraryFilter.FromUri("https://app.test" + filter.Url).Filter);
        Assert.True(filter.HasFilter);
        Assert.Contains("candidate=none", filter.Validate().Filter!.ApiQuery);
    }

    [Fact]
    public void Candidate_text_formats_masks_and_never_echoes_server_errors()
    {
        Assert.Equal("0912 345 678", CandidateText.Phone("0912345678"));
        Assert.Equal("—", CandidateText.Phone(null));
        Assert.Equal("Ứng viên #7", CandidateText.Name(7, "Nguyễn Văn An", anonymous: true));
        Assert.Equal("Nguyễn Văn An", CandidateText.Name(7, "Nguyễn Văn An", anonymous: false));
        Assert.Equal("#", CandidateText.Initials("Nguyễn Văn An", anonymous: true));
        Assert.Equal("NA", CandidateText.Initials("Nguyễn Văn An", anonymous: false));
        Assert.True(CandidateText.IsEraseConfirmed(" xóa "));
        Assert.True(CandidateText.IsEraseConfirmed("XOA"));
        Assert.False(CandidateText.IsEraseConfirmed("XO"));
        Assert.Equal("Trùng email và điện thoại", CandidateText.MatchedOn(["Email", "Phone"]));
        foreach (var status in new int?[] { 400, 401, 404, 409, 410, 429, 500, 503, null })
        {
            Assert.DoesNotContain("PRIVATE", CandidateText.Error(status));
            Assert.False(string.IsNullOrWhiteSpace(CandidateText.Error(status, writing: true)));
        }
        Assert.Contains("kiểm tra trước khi gửi lại", CandidateText.Error(null, writing: true));
    }

    [Theory]
    [InlineData("XÓA", true)]
    [InlineData(" xóa ", true)]
    [InlineData("XOA", true)]
    [InlineData("Xoá", true)]        // dấu đặt kiểu cũ (giống mẫu ZIP B24)
    [InlineData("XO", false)]
    [InlineData("DELETE", false)]
    [InlineData("", false)]
    public void Erase_keyword_accepts_only_xoa_variants(string typed, bool expected) => Assert.Equal(expected, CandidateText.IsEraseConfirmed(typed));

    [Fact]
    public void Merge_and_erase_previews_follow_api_contracts()
    {
        var merge = CandidateText.Merge(1, null, "0912345678", 2, "b@example.com", "0900000000", 3, 0);
        Assert.Equal((1, 2, 3, true, false), (merge.KeepId, merge.RemoveId, merge.CvsMoved, merge.EmailFilled, merge.PhoneFilled));
        var detail = Detail();
        var erase = CandidateText.Preview(detail);
        Assert.Equal(1, erase.CvsToAnonymize);
        Assert.Equal(1, erase.CvsAlreadyAnonymized);
        Assert.Equal(2, erase.ApplicationsToWithdraw);                         // hồ sơ đã Loại không bị rút lại
        Assert.Equal(["Backend Developer", "QA Engineer"], erase.JobsWithdrawn.Order());
        Assert.Equal("2 CV · 3 hồ sơ ứng tuyển · 3 vị trí", CandidateText.Summary(detail));
    }

    // ======================= API client: không lộ lỗi thô, đọc 409 / 400, không tự gửi lại =======================

    [Fact]
    public async Task Client_reads_duplicate_matches_and_field_errors_without_raw_server_text()
    {
        var api = new Api();
        api.Handle = (req, _) => Task.FromResult(req.Method == HttpMethod.Post
            ? Json(new { title = "PRIVATE title", detail = "PRIVATE thu@example.com", matches = new[] { new DuplicateMatchDto { CandidateId = 9, FullName = "Người Cũ", MatchedOn = ["Email"] } } }, 409)
            : Json(new { detail = "PRIVATE", errors = new Dictionary<string, string[]> { ["email"] = ["Email không đúng định dạng."] } }, 400));
        var client = Candidates(api);
        var created = await client.CreateAsync(new() { FullName = "Mới", Email = "thu@example.com" });
        Assert.False(created.IsSuccess);
        Assert.Equal(409, created.StatusCode);
        Assert.Equal(9, Assert.Single(created.Matches).CandidateId);
        Assert.DoesNotContain("PRIVATE", created.ErrorMessage);
        Assert.False(created.Uncertain);
        var updated = await client.UpdateAsync(9, new() { FullName = "X", Email = "sai" });
        Assert.Equal("Email không đúng định dạng.", updated.Errors["email"][0]);
    }

    [Fact]
    public async Task Client_marks_lost_write_as_uncertain_and_sends_exact_merge_and_erase_requests()
    {
        var api = new Api { Handle = (_, _) => throw new HttpRequestException("PRIVATE socket") };
        var lost = await Candidates(api).MergeAsync(2, 1, "HR");
        Assert.True(lost.Uncertain);
        Assert.Null(lost.StatusCode);
        Assert.Single(api.Writes);                                             // một lần gửi, không tự thử lại

        var ok = new Api();
        ok.Handle = (req, _) => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("/merge")
            ? Json(new MergeCandidateResultDto { TargetCandidateId = 1, RemovedCandidateId = 2, CvsMoved = 1 })
            : Json(new EraseCandidateResultDto { CandidateId = 1, CandidateDeleted = true }));
        var client = Candidates(ok);
        Assert.True((await client.MergeAsync(2, 1, " HR ")).IsSuccess);
        Assert.True((await client.EraseAsync(1, "HR", "  ")).IsSuccess);
        Assert.Equal(["POST /api/candidates/2/merge", "POST /api/candidates/1/erase"], ok.Writes.Select(w => w.Route));
        Assert.Contains("\"targetCandidateId\":1", ok.Writes.First().Body);
        Assert.Contains("\"confirm\":true", ok.Writes.Last().Body);
        Assert.Contains("\"note\":null", ok.Writes.Last().Body);
        string? exportPath = null;
        ok.Handle = (req, _) => { exportPath = req.RequestUri!.PathAndQuery; return Task.FromResult(Json(new { exported = true })); };
        var export = await client.ExportAsync(5);
        Assert.True(export.IsSuccess);
        await export.Data!.DisposeAsync();
        Assert.Equal("/api/candidates/5/data-export", exportPath); // B43: no actor or credential in the URL.
    }

    // ======================= Workspace: chống race, phân biệt đã xóa / không tồn tại =======================

    [Fact]
    public async Task List_workspace_ignores_stale_response_that_arrives_after_newer_request()
    {
        var slow = new TaskCompletionSource();
        var api = new Api();
        api.Handle = async (req, ct) =>
        {
            if (req.RequestUri!.Query.Contains("search=old")) { await slow.Task; return Json(People(1, 1, "Cũ")); }
            return Json(People(1, 1, "Mới"));
        };
        using var workspace = new CandidateListWorkspace(Candidates(api));
        var first = workspace.LoadAsync(new CandidateListFilter("old"));
        await workspace.LoadAsync(new CandidateListFilter("new"));
        slow.SetResult();
        await first;
        Assert.Equal("Mới", Assert.Single(workspace.Data!.Items).FullName);
        Assert.False(workspace.Loading);
    }

    [Fact]
    public async Task Detail_workspace_separates_erased_not_found_and_server_errors()
    {
        var api = new Api();
        api.Handle = (req, _) => Task.FromResult(req.RequestUri!.AbsolutePath switch
        {
            "/api/candidates/5" => Json(new { detail = "PRIVATE" }, 404),
            "/api/data-requests" when req.RequestUri.Query.Contains("candidateId=5") => Json(new List<DataRequestDto> { new() { RequestId = 3, CandidateId = 5, RequestType = "Erase", RequestTypeName = "Xóa dữ liệu", HandledBy = "HR" } }),
            "/api/data-requests" => Json(new List<DataRequestDto>()),
            "/api/candidates/6" => Json(new { detail = "PRIVATE" }, 404),
            "/api/candidates/7" => Json(new { detail = "PRIVATE" }, 503),
            "/api/candidates/8" => Json(Detail(id: 99)),
            _ => Json(new { }, 500)
        });
        using var workspace = new CandidateDetailWorkspace(Candidates(api));
        await workspace.LoadAsync(5); Assert.Equal(CandidateDetailWorkspace.State.Erased, workspace.Status); Assert.Single(workspace.Requests);
        await workspace.LoadAsync(6); Assert.Equal(CandidateDetailWorkspace.State.NotFound, workspace.Status);
        await workspace.LoadAsync(7); Assert.Equal(CandidateDetailWorkspace.State.Error, workspace.Status); Assert.DoesNotContain("PRIVATE", workspace.Error);
        await workspace.LoadAsync(8); Assert.Equal(CandidateDetailWorkspace.State.Error, workspace.Status);   // API trả nhầm người → không hiển thị
    }

    // ======================= Giao diện =======================

    [Fact]
    public async Task People_mode_lists_persons_links_details_and_never_hides_unattached_cvs()
    {
        var api = Standard();
        await using var sp = Services(api, new Nav("https://app.test/candidates"), Normal());
        await using var ui = new Ui(sp); await ui.Show<HubPage>();
        var html = await ui.Until(s => s.Contains("Trần Thị Bình"));
        Assert.Contains("/candidates/1", html);
        Assert.Contains("thu@example.com", html);
        Assert.Contains("0912 345 678", html);
        Assert.Contains("4 CV chưa gắn ứng viên", html);
        Assert.Contains("/candidates?view=cvs&candidate=none", html);
        Assert.Contains("api/cvs?candidate=none&page=1&pageSize=1", string.Join(" ", api.Calls));
        Assert.DoesNotContain("cv-table", html);                               // chế độ CV chưa mở → không gọi /api/cvs danh sách
        Assert.Single(api.Calls, c => c.StartsWith("GET /api/candidates?"));
    }

    [Fact]
    public async Task Anonymous_mode_keeps_names_emails_and_phones_out_of_the_page()
    {
        var api = Standard();
        await using var sp = Services(api, new Nav("https://app.test/candidates"), new AnonymityState());   // mặc định che tới khi đọc xong lựa chọn
        await using var ui = new Ui(sp); await ui.Show<HubPage>();
        var html = await ui.Until(s => s.Contains("Ứng viên #2"));
        foreach (var secret in new[] { "Nguyễn Văn An", "Trần Thị Bình", "thu@example.com", "0912", "NA" }) Assert.DoesNotContain(secret, html);
        Assert.Contains("Liên hệ đã ẩn", html);
    }

    [Fact]
    public async Task Switching_modes_keeps_loaded_data_and_reloads_only_after_a_write()
    {
        var api = Standard();
        var nav = new Nav("https://app.test/candidates");
        await using var sp = Services(api, nav, Normal());
        await using var ui = new Ui(sp); await ui.Show<HubPage>();
        await ui.Until(s => s.Contains("Trần Thị Bình"));
        await ui.Go(nav, "/candidates?view=cvs");
        await ui.Until(s => s.Contains("cv-b24.pdf"));
        await ui.Go(nav, "/candidates");
        await ui.Go(nav, "/candidates?view=cvs");
        await Task.Delay(50);
        Assert.Single(api.Calls, c => c.StartsWith("GET /api/candidates?"));   // quay lại chế độ đã mở: không tải trùng
        Assert.Single(api.Calls, c => c.StartsWith("GET /api/cvs?") && !c.Contains("candidate=none"));
        sp.GetRequiredService<CandidateHubState>().NotifyDataChanged();        // ví dụ: vừa gắn CV ở chế độ CV
        await ui.Go(nav, "/candidates");
        await ui.Until(_ => api.Calls.Count(c => c.StartsWith("GET /api/candidates?")) == 2);
    }

    [Fact]
    public async Task Searching_people_is_debounced_and_never_written_to_the_address()
    {
        var api = Standard();
        var nav = new Nav("https://app.test/candidates?page=2");
        await using var sp = Services(api, nav, Normal());
        await using var ui = new Ui(sp); await ui.Show<HubPage>();
        await ui.Until(s => s.Contains("Trang 2/2"));
        var firstKey = ui.Input("people-search", "N");        // gõ tiếp trước khi hết 400 ms, như người dùng thật
        await ui.Input("people-search", "Nguyễn");
        await firstKey;
        await ui.Until(_ => api.Calls.Any(c => c.Contains("search=Nguy")));
        await Task.Delay(100);
        Assert.True(api.Calls.Count(c => c.Contains("search=")) == 1, string.Join(" | ", api.Calls));   // chỉ lần gõ cuối được gửi, một lần
        Assert.Equal("https://app.test/candidates", nav.Uri);                   // về trang 1, không có từ khóa trên địa chỉ
        Assert.Equal("Nguyễn", sp.GetRequiredService<CandidateHubState>().PeopleSearch);
    }

    [Fact]
    public async Task Legacy_cvs_route_redirects_with_filters_and_replaces_history()
    {
        var nav = new Nav("https://app.test/cvs?stage=Rejected&page=3");
        await using var sp = Services(Standard(), nav, Normal());
        await using var ui = new Ui(sp); await ui.Show<RedirectPage>();
        Assert.Equal("https://app.test/candidates?view=cvs&stage=Rejected&page=3", nav.Uri);
        Assert.True(nav.LastReplace);
    }

    [Fact]
    public async Task Detail_groups_applications_by_cv_with_per_application_scores_timeline_and_duplicates()
    {
        var api = Standard();
        await using var sp = Services(api, new Nav("https://app.test/candidates/1"), Normal());
        await using var ui = new Ui(sp); await ui.Show<DetailPage>(new() { ["Id"] = 1 });
        var html = await ui.Until(s => s.Contains("Nguyễn Văn An"));
        Assert.Contains("2 CV", html); Assert.Contains("3 hồ sơ ứng tuyển", html);
        Assert.Contains("cv-moi.pdf", html); Assert.Contains("Hồ sơ đã ẩn danh", html);
        Assert.Contains("Backend Developer", html); Assert.Contains("QA Engineer", html); Assert.Contains("Data Analyst", html);
        Assert.Contains("điểm lần ứng tuyển này", html);                                 // điểm gắn với từng lần ứng tuyển
        Assert.DoesNotContain("Điểm của ứng viên", html);
        Assert.Contains("Có thể là cùng một người", html); Assert.Contains("Người Trùng", html);
        Assert.Contains("Xem file", html);
        Assert.Contains("Tải về", html);
        Assert.DoesNotContain("api/cvs/" + CvA + "/file", html); // B43: no unauthenticated API href in HTML.
        Assert.DoesNotContain("api/cvs/" + CvB + "/file", html);               // CV đã ẩn danh hóa: không mở file
        Assert.Contains("Đã gửi email", html);                                 // nhật ký yêu cầu
        Assert.Contains("HR - Lan", html);                                     // dòng thời gian mở sẵn cho hồ sơ mới nhất
    }

    [Fact]
    public async Task Detail_in_anonymous_mode_hides_identity_and_locks_identity_actions()
    {
        await using var sp = Services(Standard(), new Nav("https://app.test/candidates/1"), new AnonymityState());
        await using var ui = new Ui(sp); await ui.Show<DetailPage>(new() { ["Id"] = 1 });
        var html = await ui.Until(s => s.Contains("Ứng viên #1"));
        foreach (var secret in new[] { "Nguyễn Văn An", "thu@example.com", "0912", "cv-moi.pdf", "Người Trùng", "Đã gửi email", "HR - Lan" })
            Assert.DoesNotContain(secret, html);
        Assert.Contains("Đang bật Ẩn danh", html);
        Assert.DoesNotContain("data-export", html);
    }

    [Fact]
    public async Task Detail_after_erase_or_for_missing_person_shows_the_right_state()
    {
        await using var sp = Services(Standard(), new Nav("https://app.test/candidates/5"), Normal());
        await using var ui = new Ui(sp); await ui.Show<DetailPage>(new() { ["Id"] = 5 });
        var html = await ui.Until(s => s.Contains("đã được xóa"));
        Assert.Contains("Nhật ký yêu cầu", html); Assert.Contains("Theo yêu cầu ứng viên", html);
        await using var missing = new Ui(sp); await missing.Show<DetailPage>(new() { ["Id"] = 404 });
        Assert.Contains("Không có ứng viên #404", await missing.Until(s => s.Contains("#404")));
    }

    [Fact]
    public async Task Editor_validates_locally_shows_duplicates_and_sends_allow_duplicate_only_after_explicit_choice()
    {
        var api = Standard();
        CandidateDto? saved = null;
        await using var sp = Services(api, new Nav(), Normal());
        await using var ui = new Ui(sp);
        await ui.Show<EditorDialog>(new() { ["OnSaved"] = EventCallback.Factory.Create<CandidateDto>(this, c => saved = c) });
        await ui.Click("editor-save");
        Assert.Contains("Họ tên là bắt buộc", await ui.All());
        Assert.Empty(api.Writes);                                              // dữ liệu chưa hợp lệ → không gọi API
        await ui.Input("editor-name", "Người Mới"); await ui.Input("editor-email", "thu@example.com");
        await ui.Click("editor-save");
        Assert.Contains("Có thể trùng người đã có", await ui.Until(s => s.Contains("Người Cũ")));
        Assert.Single(api.Writes);
        await ui.Click("editor-allow-duplicate");
        await ui.Until(_ => saved is not null);
        Assert.Equal(2, api.Writes.Count);
        Assert.Contains("\"allowDuplicate\":false", api.Writes.First().Body);
        Assert.Contains("\"allowDuplicate\":true", api.Writes.Last().Body);
        Assert.Contains("\"email\":\"thu@example.com\"", api.Writes.Last().Body);
    }

    [Fact]
    public async Task Editor_never_resends_after_an_uncertain_write()
    {
        var api = Standard();
        api.Handle = (req, _) => req.Method == HttpMethod.Post ? throw new HttpRequestException("lost") : Task.FromResult(Json(new { }, 404));
        await using var sp = Services(api, new Nav(), Normal());
        await using var ui = new Ui(sp); await ui.Show<EditorDialog>();
        await ui.Input("editor-name", "Người Mới"); await ui.Input("editor-phone", "0912 345 678");
        await ui.Click("editor-save");
        Assert.Contains("Chưa xác nhận được kết quả lưu", await ui.All());
        await ui.Click("editor-save");
        Assert.Single(api.Writes);
    }

    [Fact]
    public async Task Merge_needs_choice_review_and_confirmation_and_never_retries_after_409()
    {
        var api = Standard();
        api.MergeStatus = 409;
        string? uncertain = null;
        await using var sp = Services(api, new Nav(), Normal());
        await using var ui = new Ui(sp);
        await ui.Show<MergeDialog>(new()
        {
            ["Current"] = Detail(), ["Other"] = new DuplicateMatchDto { CandidateId = 2, FullName = "Người Trùng", Email = "thu@example.com", MatchedOn = ["Email"], CvCount = 1 },
            ["Actor"] = "HR - Lan", ["OnUncertain"] = EventCallback.Factory.Create<string>(this, m => uncertain = m)
        });
        Assert.Contains("Người Trùng", await ui.All());
        await ui.Click("merge-next");
        Assert.Contains("sẽ bị xóa sau khi chuyển dữ liệu", await ui.All());
        await ui.Click("merge-submit");
        Assert.Empty(api.Writes);                                              // chưa tick xác nhận → không gửi
        await ui.Change("merge-confirm", "true");
        await ui.Click("merge-submit");
        await ui.Until(_ => uncertain is not null);
        Assert.Equal("POST /api/candidates/2/merge", Assert.Single(api.Writes).Route);   // giữ người đang xem: người kia là nguồn
        await ui.Click("merge-submit");
        Assert.Single(api.Writes);
    }

    [Fact]
    public async Task Erase_uses_the_verified_actor_and_requires_keyword_before_one_confirmed_request()
    {
        var api = Standard();
        EraseCandidateResultDto? done = null;
        await using var sp = Services(api, new Nav(), Normal());
        await using var ui = new Ui(sp);
        await ui.Show<EraseDialog>(new() { ["Candidate"] = Detail(), ["OnErased"] = EventCallback.Factory.Create<EraseCandidateResultDto>(this, r => done = r) });
        var step1 = await ui.All();
        Assert.Contains("Ẩn danh hóa 1 CV", step1); Assert.Contains("Rút 2 hồ sơ", step1);
        Assert.Contains("HR - Lan", step1);
        Assert.DoesNotContain("erase-handled-by", step1);
        await ui.Click("erase-next");
        Assert.Contains("Bước 2/2", await ui.All());
        await ui.Input("erase-keyword", "XO");
        await ui.Click("erase-submit");
        Assert.Empty(api.Writes);
        await ui.Input("erase-keyword", "xóa");
        await ui.Click("erase-submit");
        await ui.Until(_ => done is not null);
        var write = Assert.Single(api.Writes);
        Assert.Equal("POST /api/candidates/1/erase", write.Route);
        Assert.Contains("\"confirm\":true", write.Body);
        Assert.Contains("\"handledBy\":\"HR - Lan\"", write.Body);
    }

    // ======================= Dữ liệu giả và hạ tầng =======================

    private static readonly Guid CvA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CvB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTime T = new(2026, 11, 18, 3, 0, 0, DateTimeKind.Utc);

    /// <summary>Một người, 2 CV (một CV đã ẩn danh); CV A nộp 2 vị trí, CV B nộp 1 vị trí; 1 người có thể trùng.</summary>
    private static CandidateDetailDto Detail(int id = 1) => new()
    {
        CandidateId = id, FullName = "Nguyễn Văn An", Email = "thu@example.com", PhoneNumber = "0912345678", CreatedAt = T,
        Cvs = [new() { CvId = CvA, FileName = "cv-moi.pdf", UploadedAt = T, ExtractionStatus = "Completed", SourceChannel = "Email", RetainUntil = T.AddDays(60) },
               new() { CvId = CvB, FileName = "Hồ sơ đã ẩn danh", UploadedAt = T.AddDays(-40), ExtractionStatus = "Completed", AnonymizedAt = T.AddDays(-1) }],
        Applications =
        [
            new() { ApplicationId = 11, CvId = CvA, JobId = 1, JobTitle = "Backend Developer", Stage = "Reviewing", AppliedAt = T, LatestFinalScore = 82, LatestFitLabel = "Phù hợp",
                    Events = [new() { EventId = 1, FromStage = "New", ToStage = "Reviewing", Actor = "HR - Lan", CreatedAt = T.AddHours(2) }] },
            new() { ApplicationId = 12, CvId = CvA, JobId = 2, JobTitle = "QA Engineer", Stage = "Interview", AppliedAt = T.AddHours(-1) },
            new() { ApplicationId = 13, CvId = CvB, JobId = 3, JobTitle = "Data Analyst", Stage = "Rejected", RejectReason = "MissingSkills", AppliedAt = T.AddDays(-40) }
        ],
        PossibleDuplicates = [new() { CandidateId = 2, FullName = "Người Trùng", Email = "thu@example.com", MatchedOn = ["Email"], CvCount = 1 }],
        DataRequests = [new() { RequestId = 1, CandidateId = id, RequestType = "Export", RequestTypeName = "Xem / xuất dữ liệu", HandledBy = "HR - Lan", Note = "Đã gửi email", CreatedAt = T }]
    };

    private static CandidatePageDto People(int page, int total, params string[] names) => new()
    {
        Page = page, PageSize = 20, TotalCount = total, TotalPages = (total + 19) / 20,
        Items = [.. names.Select((n, i) => new CandidateListItemDto { CandidateId = i + 1, FullName = n, Email = "thu@example.com", PhoneNumber = "0912345678", CvCount = 2, ApplicationCount = 3, CreatedAt = T, LastUploadedAt = T })]
    };

    /// <summary>API giả: danh sách người, chi tiết #1, đã xóa #5, CV, gợi ý trùng khi tạo, gộp, xóa.</summary>
    private static Api Standard()
    {
        var api = new Api();
        api.Handle = async (req, ct) =>
        {
            var path = req.RequestUri!.AbsolutePath; var query = req.RequestUri.Query;
            if (path == "/api/jobs") return Json(new List<JobDto>());
            if (path == "/api/candidates" && req.Method == HttpMethod.Get)
            {
                var page = query.Contains("page=2") ? 2 : 1;
                return Json(page == 2 ? People(2, 25, "Nguyễn Văn An") : People(1, 2, "Nguyễn Văn An", "Trần Thị Bình"));
            }
            if (path == "/api/candidates" && req.Method == HttpMethod.Post)
            {
                var body = (await req.Content!.ReadFromJsonAsync<CandidateRequest>(ct))!;
                return body.AllowDuplicate ? Json(new CandidateDto { CandidateId = 30, FullName = body.FullName! }, 201)
                    : Json(new { detail = "PRIVATE", matches = new[] { new DuplicateMatchDto { CandidateId = 9, FullName = "Người Cũ", MatchedOn = ["Email"], CvCount = 1 } } }, 409);
            }
            if (path == "/api/candidates/1") return Json(Detail());
            if (path.EndsWith("/merge")) return api.MergeStatus == 200 ? Json(new MergeCandidateResultDto { TargetCandidateId = 1, RemovedCandidateId = 2, CvsMoved = 1 }) : Json(new { detail = "PRIVATE" }, api.MergeStatus);
            if (path.EndsWith("/erase")) return Json(new EraseCandidateResultDto { RequestId = 7, CandidateId = 1, CvsAnonymized = 1, CandidateDeleted = true, HandledAt = T });
            if (path.StartsWith("/api/candidates/")) return Json(new { detail = "PRIVATE" }, 404);
            if (path == "/api/data-requests")
                return Json(query.Contains("candidateId=5")
                    ? new List<DataRequestDto> { new() { RequestId = 4, CandidateId = 5, RequestType = "Erase", RequestTypeName = "Xóa dữ liệu", HandledBy = "HR", ResultSummary = "Theo yêu cầu ứng viên: ẩn danh hóa 2 CV", CreatedAt = T } }
                    : new List<DataRequestDto>());
            if (path == "/api/cvs")
                return Json(query.Contains("candidate=none")
                    ? new CvPageDto { Page = 1, PageSize = 1, TotalCount = 4, TotalPages = 4, Items = [] }
                    : new CvPageDto { Page = 1, PageSize = 20, TotalCount = 1, TotalPages = 1, Items = [new() { CvId = CvA, FileName = "cv-b24.pdf", UploadedAt = T, ExtractionStatus = "Completed", CandidateId = 1, CandidateName = "Nguyễn Văn An" }] });
            return Json(new { detail = "PRIVATE missing" }, 404);
        };
        return api;
    }

    private static HttpResponseMessage Json(object value, int status = 200) => new((HttpStatusCode)status) { Content = JsonContent.Create(value, value.GetType()) };
    private static AnonymityState Normal() { var s = new AnonymityState(); s.SetAsync(false).GetAwaiter().GetResult(); return s; }
    private static CandidateApiClient Candidates(HttpMessageHandler handler) => new(new(handler) { BaseAddress = new("http://localhost/") }, NullLogger<CandidateApiClient>.Instance);
    private static ServiceProvider Services(Api api, Nav nav, AnonymityState anonymity)
    {
        var services = new ServiceCollection().AddLogging().AddSingleton<NavigationManager>(nav).AddSingleton(anonymity)
            .AddSingleton(new CvApiClient(new(api) { BaseAddress = new("http://localhost/") }, Options.Create(new CvUploadOptions()), NullLogger<CvApiClient>.Instance))
            .AddSingleton(new JobApiClient(new(api) { BaseAddress = new("http://localhost/") }, NullLogger<JobApiClient>.Instance))
            .AddSingleton(Candidates(api)).AddSingleton(new CandidateHubState()).AddSingleton(B43UiTestSupport.Actor())
            .AddSingleton(B43UiTestSupport.Session()).AddSingleton<FileDownloadService>()
            .AddSingleton<IJSRuntime>(new Js());
        services.AddSupplyValueFromQueryProvider();
        return services.BuildServiceProvider();
    }

    private sealed class Api : HttpMessageHandler
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public List<(string Route, string Body)> Writes { get; } = [];
        public int MergeStatus { get; set; } = 200;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handle { get; set; } = (_, _) => Task.FromResult(Json(new { }, 404));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Enqueue($"{request.Method} {request.RequestUri!.PathAndQuery}");
            if (request.Method != HttpMethod.Get)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
                lock (Writes) Writes.Add(($"{request.Method} {request.RequestUri.AbsolutePath}", body));
            }
            return await Handle(request, ct);
        }
    }

    private sealed class Nav : NavigationManager
    {
        public bool LastReplace { get; private set; }
        public Nav(string uri = "https://app.test/candidates") => Initialize("https://app.test/", uri);
        protected override void NavigateToCore(string uri, bool forceLoad) { Uri = ToAbsoluteUri(uri).AbsoluteUri; NotifyLocationChanged(false); }
        protected override void NavigateToCore(string uri, NavigationOptions options) { LastReplace = options.ReplaceHistoryEntry; NavigateToCore(uri, options.ForceLoad); }
        public void Go(string uri) { LastReplace = false; NavigateToCore(uri, false); }
    }

    private sealed class Js : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }

    private sealed class Ui(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        private int root = -1;
        protected override RendererInfo RendererInfo { get; } = new("B24-test", isInteractive: false);
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception error) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        public Task Show<T>(Dictionary<string, object?>? parameters = null) where T : IComponent => Dispatcher.InvokeAsync(async () =>
        { root = AssignRootComponentId(InstantiateComponent(typeof(T))); await RenderRootComponentAsync(root, ParameterView.FromDictionary(parameters ?? [])); });
        public Task Go(Nav nav, string uri) => Dispatcher.InvokeAsync(() => nav.Go(uri));
        public Task Click(string id) => Fire(id, "onclick", new MouseEventArgs());
        public Task Change(string id, object value) => Fire(id, "onchange", new ChangeEventArgs { Value = value is "true" ? true : value });
        public Task Input(string id, string value) => Fire(id, "oninput", new ChangeEventArgs { Value = value });
        public Task<string> All() => Dispatcher.InvokeAsync(() => string.Concat(Trees(root).SelectMany(f => f).Select(f => f.FrameType switch
        { RenderTreeFrameType.Text => f.TextContent, RenderTreeFrameType.Markup => f.MarkupContent, RenderTreeFrameType.Attribute => f.AttributeValue as string, _ => null })));
        public async Task<string> Until(Func<string, bool> predicate)
        {
            var time = Stopwatch.StartNew();
            while (true)
            {
                var text = await All();
                if (predicate(text)) return text;
                if (time.ElapsedMilliseconds > 10000) throw new TimeoutException(text);
                await Task.Delay(10);
            }
        }
        private Task Fire(string id, string name, EventArgs args) => Dispatcher.InvokeAsync(() =>
        {
            foreach (var frames in Trees(root))
                for (var i = 0; i < frames.Length; i++)
                {
                    if (frames[i].FrameType != RenderTreeFrameType.Element) continue;
                    var attributes = frames.Skip(i + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute).ToArray();
                    if (!attributes.Any(a => a.AttributeName == "id" && a.AttributeValue?.ToString() == id)) continue;
                    if (attributes.Any(a => a.AttributeName == "disabled" && a.AttributeValue is true)) return Task.CompletedTask;   // nút bị khóa: như trình duyệt, không có sự kiện
                    return DispatchEventAsync(attributes.Single(a => a.AttributeName == name).AttributeEventHandlerId, null, args);
                }
            throw new InvalidOperationException("Missing control: " + id);
        });
        private IEnumerable<RenderTreeFrame[]> Trees(int id)
        {
            if (id < 0) yield break;
            var frames = GetCurrentRenderTreeFrames(id); var copy = frames.Array.Take(frames.Count).ToArray(); yield return copy;
            foreach (var child in copy.Where(f => f.FrameType == RenderTreeFrameType.Component)) foreach (var nested in Trees(child.ComponentId)) yield return nested;
        }
    }
}
#pragma warning restore BL0006
