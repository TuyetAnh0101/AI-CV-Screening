using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;
using SmartCv.Tests.Jobs;

namespace SmartCv.Tests.Candidates;

/// <summary>Real HTTP pipeline, isolated EF InMemory and synthetic data; never SQL, OCR or Gemini.</summary>
public class CandidateEndpointTests
{
    private sealed record Seed(int Source, int Target, Guid A, Guid B, Guid Free, Guid Anonymized, int Application);
    private static readonly DateTime T0 = new(2026, 11, 18, 3, 0, 0, DateTimeKind.Utc);

    private static async Task<(WebApplicationFactory<Program>, HttpClient, Seed)> StartAsync()
    {
        var factory = JobCrudEndpointTests.Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        await db.Database.EnsureCreatedAsync();
        var source = new Candidate { FullName = "Nguyễn Văn An", Email = "AN@MAIL.COM", PhoneNumber = "0912345678", CreatedAt = T0 };
        var target = new Candidate { FullName = "Trần Thị Bình", Email = "binh@example.com", CreatedAt = T0 };
        Cv Cv(Candidate? candidate = null)
        {
            var cv = JobCrudEndpointTests.Cv();
            cv.Candidate = candidate; cv.UploadedAt = T0; cv.OriginalFileName = "synthetic.pdf";
            cv.ExtractionStatus = ExtractionStatuses.Completed;
            cv.ExtractedText = "NGUYỄN VĂN AN\nEmail: An@Mail.com | Phone: +84 912 345 678";
            cv.StoredPath = "PRIVATE_PATH"; return cv;
        }
        var a = Cv(source); var b = Cv(source); var free = Cv(); var anonymized = Cv();
        anonymized.AnonymizedAt = T0; anonymized.OriginalFileName = "PRIVATE_ERASED";
        db.AddRange(a, b, free, anonymized, target);
        var app = new Application { Cv = a, JobId = 1, Stage = ApplicationStages.Interview, CreatedAt = T0, StageUpdatedAt = T0.AddMinutes(2) };
        var second = new Application { Cv = b, JobId = 2, Stage = ApplicationStages.Rejected, RejectReason = RejectReasons.MissingSkills, CreatedAt = T0 };
        db.AddRange(app, second);
        db.ApplicationEvents.AddRange(
            new ApplicationEvent { Application = app, FromStage = ApplicationStages.New, ToStage = ApplicationStages.Reviewing, Actor = "hr.synthetic", Note = "first", CreatedAt = T0 },
            new ApplicationEvent { Application = app, FromStage = ApplicationStages.Reviewing, ToStage = ApplicationStages.Interview, Actor = "hr.synthetic", Note = "second", CreatedAt = T0.AddMinutes(2) });
        var prompt = new PromptVersion { VersionName = "A24 synthetic", SystemPrompt = "synthetic" };
        foreach (var (type, score, minutes) in new[] { (RunTypes.Screening, 70m, 1), (RunTypes.Screening, 85m, 2), (RunTypes.PromptTest, 99m, 3) })
        {
            var run = new EvaluationRun { RunType = type, Status = RunStatuses.Completed, PromptVersion = prompt, ModelName = "fixture",
                Temperature = .2m, KeywordWeight = .3m, AiWeight = .7m, PassThreshold = 70, CreatedAt = T0.AddMinutes(minutes) };
            run.Evaluations.Add(new Evaluation { Application = app, Status = EvaluationStatuses.Completed, FinalScore = score, FitLabel = "Phù hợp", CreatedAt = run.CreatedAt });
            db.Add(run);
        }
        await db.SaveChangesAsync();
        return (factory, factory.CreateClient(), new(source.CandidateId, target.CandidateId, a.CvId, b.CvId, free.CvId, anonymized.CvId, app.ApplicationId));
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
    private static Task<JsonElement> GetAsync(HttpClient client, string path) => JsonResponseAsync(client.GetAsync(path));
    private static async Task<JsonElement> JsonResponseAsync(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; return await JsonAsync(response);
    }
    private static CandidateRequest Request(string email = "new@example.com") => new() { FullName = "  Nguyễn   Thử  ", Email = email, PhoneNumber = "+84 987 654 321" };
    private static async Task<(int Candidates, int Cvs, int Apps, int Events, decimal?[] Scores)> SnapshotAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        return (await db.Candidates.CountAsync(), await db.Cvs.CountAsync(), await db.Applications.CountAsync(),
            await db.ApplicationEvents.CountAsync(), await db.Evaluations.OrderBy(e => e.EvaluationId).Select(e => e.FinalScore).ToArrayAsync());
    }

    [Fact]
    public async Task Create_normalizes_contact_and_returns_location_and_UTC()
    {
        var (factory, client, _) = await StartAsync(); using var f = factory; using var c = client;
        using var response = await client.PostAsJsonAsync("/api/candidates", Request("NEW@EXAMPLE.COM"));
        var item = await JsonAsync(response, HttpStatusCode.Created);
        Assert.Equal("Nguyễn Thử", item.GetProperty("fullName").GetString());
        Assert.Equal("new@example.com", item.GetProperty("email").GetString());
        Assert.Equal("0987654321", item.GetProperty("phoneNumber").GetString());
        Assert.EndsWith("Z", item.GetProperty("createdAt").GetString());
        Assert.Equal("/api/candidates/" + item.GetProperty("candidateId").GetInt32(), response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData(null, null, null, "fullName,contact")]
    [InlineData("", "abc", "12345", "fullName,email,phoneNumber")]
    [InlineData("Valid Name", null, null, "contact")]
    [InlineData("Valid Name", "a@b.com", "0١٢٣٤٥٦٧٨٩", "phoneNumber")]
    public async Task Invalid_contact_returns_field_errors_without_saving(string? name, string? email, string? phone, string fields)
    {
        var (factory, client, _) = await StartAsync(); using var f = factory; using var c = client;
        var before = await SnapshotAsync(f);
        using var response = await client.PostAsJsonAsync("/api/candidates", new { fullName = name, email, phoneNumber = phone });
        var error = await JsonAsync(response, HttpStatusCode.BadRequest);
        foreach (var field in fields.Split(',')) Assert.True(error.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(before.Candidates, (await SnapshotAsync(f)).Candidates);
    }

    [Theory]
    [InlineData("POST", "/api/candidates")]
    [InlineData("PUT", "/api/candidates/1")]
    [InlineData("POST", "/api/candidates/1/merge")]
    public async Task Null_JSON_body_is_a_400(string method, string path)
    {
        var (factory, client, _) = await StartAsync(); using var f = factory; using var c = client;
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = new StringContent("null", Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request);
        var error = await JsonAsync(response, HttpStatusCode.BadRequest);
        Assert.True(error.GetProperty("errors").TryGetProperty("body", out _));
    }

    [Fact]
    public async Task Duplicate_creation_requires_explicit_HR_override_and_matches_ignore_email_case()
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        var request = Request("an@mail.com"); request.PhoneNumber = "+84 912 345 678";
        using var conflict = await client.PostAsJsonAsync("/api/candidates", request);
        var error = await JsonAsync(conflict, HttpStatusCode.Conflict);
        var match = Assert.Single(error.GetProperty("matches").EnumerateArray());
        Assert.Equal(seed.Source, match.GetProperty("candidateId").GetInt32());
        Assert.Equal(new[] { "Email", "Phone" }, match.GetProperty("matchedOn").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(2, match.GetProperty("cvCount").GetInt32());
        request.AllowDuplicate = true;
        using var confirmed = await client.PostAsJsonAsync("/api/candidates", request);
        await JsonAsync(confirmed, HttpStatusCode.Created);
        Assert.Equal(3, (await SnapshotAsync(f)).Candidates);
    }

    [Fact]
    public async Task Contact_update_conflicts_without_mutation_and_can_be_confirmed_or_cleared()
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        var request = Request("AN@MAIL.COM"); request.PhoneNumber = null;
        using var rejected = await client.PutAsJsonAsync($"/api/candidates/{seed.Target}", request);
        await JsonAsync(rejected, HttpStatusCode.Conflict);
        Assert.Equal("binh@example.com", (await GetAsync(client, $"/api/candidates/{seed.Target}")).GetProperty("email").GetString());
        request.AllowDuplicate = true;
        using var saved = await client.PutAsJsonAsync($"/api/candidates/{seed.Target}", request);
        Assert.Equal("an@mail.com", (await JsonAsync(saved)).GetProperty("email").GetString());
        request.AllowDuplicate = false; request.FullName = "New Name";
        using var rename = await client.PutAsJsonAsync($"/api/candidates/{seed.Target}", request);
        await JsonAsync(rename); // unchanged contact does not demand another override
        request.Email = null; request.PhoneNumber = "0900000000";
        using var clear = await client.PutAsJsonAsync($"/api/candidates/{seed.Target}", request);
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(clear)).GetProperty("email").ValueKind);
    }

    [Theory]
    [InlineData("Nguyễn Văn", 1)]
    [InlineData("an@mail.com", 1)]
    [InlineData("AN@MAIL.COM", 1)]
    [InlineData("+84 912 345 678", 1)]
    [InlineData("0912.345.678", 1)]
    [InlineData("not-found", 0)]
    public async Task List_searches_name_email_and_normalized_phone(string search, int count)
    {
        var (factory, client, _) = await StartAsync(); using var f = factory; using var c = client;
        var page = await GetAsync(client, "/api/candidates?search=" + Uri.EscapeDataString(search));
        Assert.Equal(count, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(count, page.GetProperty("items").GetArrayLength());
        if (count > 0)
        {
            var row = page.GetProperty("items")[0];
            Assert.Equal(2, row.GetProperty("cvCount").GetInt32());
            Assert.Equal(2, row.GetProperty("applicationCount").GetInt32());
            Assert.EndsWith("Z", row.GetProperty("lastUploadedAt").GetString());
        }
    }

    [Theory]
    [InlineData("page=0", "page")]
    [InlineData("page=-1", "page")]
    [InlineData("page=abc", "page")]
    [InlineData("page=2147483648", "page")]
    [InlineData("page=2147483647&pageSize=100", "page")]
    [InlineData("page=1&page=2", "page")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=101", "pageSize")]
    [InlineData("pageSize=1.5", "pageSize")]
    [InlineData("search=a&search=b", "search")]
    public async Task List_rejects_invalid_repeated_and_overflowing_queries(string query, string field)
    {
        var (factory, client, _) = await StartAsync(); using var f = factory; using var c = client;
        using var response = await client.GetAsync("/api/candidates?" + query);
        var error = await JsonAsync(response, HttpStatusCode.BadRequest);
        Assert.True(error.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Paging_is_stable_and_empty_pages_do_not_overflow_or_leak_private_CV_fields()
    {
        var (factory, client, _) = await StartAsync(); using var f = factory; using var c = client;
        var one = await GetAsync(client, "/api/candidates?pageSize=1");
        var two = await GetAsync(client, "/api/candidates?pageSize=1&page=2");
        Assert.NotEqual(one.GetProperty("items")[0].GetProperty("candidateId").GetInt32(), two.GetProperty("items")[0].GetProperty("candidateId").GetInt32());
        Assert.Equal(JsonValueKind.Null, one.GetProperty("items")[0].GetProperty("lastUploadedAt").ValueKind);
        Assert.Empty((await GetAsync(client, "/api/candidates?page=2147483647&pageSize=1")).GetProperty("items").EnumerateArray());
        Assert.DoesNotContain("PRIVATE", one.GetRawText());
        Assert.DoesNotContain("extractedText", one.GetRawText());
    }

    [Fact]
    public async Task Detail_unifies_all_CVs_jobs_timelines_and_latest_screening_without_writes()
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        var before = await SnapshotAsync(f);
        var detail = await GetAsync(client, $"/api/candidates/{seed.Source}");
        Assert.Equal(2, detail.GetProperty("cvs").GetArrayLength());
        Assert.Equal(2, detail.GetProperty("applications").GetArrayLength());
        var app = detail.GetProperty("applications").EnumerateArray().Single(a => a.GetProperty("applicationId").GetInt32() == seed.Application);
        Assert.Equal(85m, app.GetProperty("latestFinalScore").GetDecimal()); // newer prompt test has 99
        Assert.Equal("Interview", app.GetProperty("stage").GetString());
        Assert.Equal(new[] { "first", "second" }, app.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("note").GetString()));
        Assert.All(app.GetProperty("events").EnumerateArray(), e => { Assert.Equal("hr.synthetic", e.GetProperty("actor").GetString()); Assert.EndsWith("Z", e.GetProperty("createdAt").GetString()); });
        Assert.DoesNotContain("PRIVATE_PATH", detail.GetRawText());
        Assert.DoesNotContain("extractedText", detail.GetRawText());
        var after = await SnapshotAsync(f); Assert.Equal(before.Candidates, after.Candidates); Assert.Equal(before.Scores, after.Scores); Assert.Equal(before.Events, after.Events);
    }

    [Fact]
    public async Task Suggestion_is_read_only_then_duplicate_attachment_requires_a_decision()
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        var suggestion = await GetAsync(client, $"/api/cvs/{seed.Free}/candidate-suggestion");
        Assert.Equal("Nguyễn Văn An", suggestion.GetProperty("fullName").GetString());
        Assert.Equal("an@mail.com", suggestion.GetProperty("email").GetString());
        Assert.Equal("0912345678", suggestion.GetProperty("phoneNumber").GetString());
        Assert.Single(suggestion.GetProperty("matches").EnumerateArray());
        using var response = await client.PutAsJsonAsync($"/api/cvs/{seed.Free}/candidate", new { fullName = "Nguyễn Văn An", email = "an@mail.com" });
        var decision = await JsonAsync(response);
        Assert.Equal("NeedsDecision", decision.GetProperty("outcome").GetString());
        Assert.Equal(2, (await SnapshotAsync(f)).Candidates);
        using var chosen = await client.PutAsJsonAsync($"/api/cvs/{seed.Free}/candidate", new { candidateId = seed.Source });
        var attached = await JsonAsync(chosen);
        Assert.Equal("Attached", attached.GetProperty("outcome").GetString());
        Assert.False(attached.GetProperty("candidateCreated").GetBoolean());
        Assert.Equal(3, (await GetAsync(client, $"/api/candidates/{seed.Source}")).GetProperty("cvs").GetArrayLength());
    }

    [Fact]
    public async Task Attach_creates_normalized_contact_but_repeated_create_returns_a_decision()
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        var body = new { fullName = "  New   Person ", email = "new@example.com", phoneNumber = "+84 987 654 321" };
        using var response = await client.PutAsJsonAsync($"/api/cvs/{seed.Free}/candidate", body);
        var attached = await JsonAsync(response);
        Assert.True(attached.GetProperty("candidateCreated").GetBoolean());
        Assert.Equal("0987654321", attached.GetProperty("candidate").GetProperty("phoneNumber").GetString());
        using var repeated = await client.PutAsJsonAsync($"/api/cvs/{seed.Free}/candidate", body);
        Assert.Equal("NeedsDecision", (await JsonAsync(repeated)).GetProperty("outcome").GetString());
        Assert.Equal(3, (await SnapshotAsync(f)).Candidates);
    }

    [Fact]
    public async Task Reassignment_only_removes_previous_candidate_after_their_last_CV_and_is_idempotent()
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        using var first = await client.PutAsJsonAsync($"/api/cvs/{seed.A}/candidate", new { candidateId = seed.Target });
        Assert.False((await JsonAsync(first)).GetProperty("previousCandidateRemoved").GetBoolean());
        using var second = await client.PutAsJsonAsync($"/api/cvs/{seed.B}/candidate", new { candidateId = seed.Target });
        Assert.True((await JsonAsync(second)).GetProperty("previousCandidateRemoved").GetBoolean());
        using var removed = await client.GetAsync($"/api/candidates/{seed.Source}"); await JsonAsync(removed, HttpStatusCode.NotFound);
        using var again = await client.PutAsJsonAsync($"/api/cvs/{seed.B}/candidate", new { candidateId = seed.Target });
        Assert.False((await JsonAsync(again)).GetProperty("previousCandidateRemoved").GetBoolean());
        Assert.Equal(1, (await SnapshotAsync(f)).Candidates);
    }

    [Fact]
    public async Task Confirm_new_is_explicit_and_anonymized_CVs_cannot_regain_identity()
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        using var response = await client.PutAsJsonAsync($"/api/cvs/{seed.Free}/candidate", new { fullName = "Different Person", email = "an@mail.com", confirmNew = true });
        Assert.True((await JsonAsync(response)).GetProperty("candidateCreated").GetBoolean());
        using var blocked = await client.PutAsJsonAsync($"/api/cvs/{seed.Anonymized}/candidate", new { candidateId = seed.Target });
        await JsonAsync(blocked, HttpStatusCode.Conflict);
        using var hidden = await client.GetAsync($"/api/cvs/{seed.Anonymized}/candidate-suggestion");
        await JsonAsync(hidden, HttpStatusCode.Gone);
        Assert.Equal(3, (await SnapshotAsync(f)).Candidates);
    }

    [Fact]
    public async Task Attach_rejects_mixed_modes_invalid_ids_and_null_without_mutation()
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        foreach (var body in new object[] { new { candidateId = 0 }, new { candidateId = seed.Source, fullName = "Ambiguous" }, new { candidateId = seed.Source, confirmNew = true } })
        {
            using var response = await client.PutAsJsonAsync($"/api/cvs/{seed.Free}/candidate", body);
            await JsonAsync(response, HttpStatusCode.BadRequest);
        }
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/cvs/{seed.Free}/candidate") { Content = new StringContent("null", Encoding.UTF8, "application/json") };
        using var empty = await client.SendAsync(request); await JsonAsync(empty, HttpStatusCode.BadRequest);
        Assert.Equal(2, (await SnapshotAsync(f)).Candidates);
    }

    [Fact]
    public async Task Merge_preserves_all_applications_events_and_scores_and_only_fills_missing_contact()
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        var before = await SnapshotAsync(f);
        using var response = await client.PostAsJsonAsync($"/api/candidates/{seed.Source}/merge", new { targetCandidateId = seed.Target, actor = "hr.synthetic" });
        var result = await JsonAsync(response);
        Assert.Equal(2, result.GetProperty("cvsMoved").GetInt32());
        var target = await GetAsync(client, $"/api/candidates/{seed.Target}");
        Assert.Equal("binh@example.com", target.GetProperty("email").GetString());
        Assert.Equal("0912345678", target.GetProperty("phoneNumber").GetString());
        Assert.Equal(2, target.GetProperty("cvs").GetArrayLength()); Assert.Equal(2, target.GetProperty("applications").GetArrayLength());
        using var missing = await client.GetAsync($"/api/candidates/{seed.Source}"); await JsonAsync(missing, HttpStatusCode.NotFound);
        var after = await SnapshotAsync(f);
        Assert.Equal(before.Cvs, after.Cvs); Assert.Equal(before.Apps, after.Apps);
        Assert.Equal(before.Events, after.Events); Assert.Equal(before.Scores, after.Scores);
    }

    [Fact]
    public async Task Merge_into_self_or_missing_target_cannot_move_any_CV()
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        using var self = await client.PostAsJsonAsync($"/api/candidates/{seed.Source}/merge", new { targetCandidateId = seed.Source });
        await JsonAsync(self, HttpStatusCode.BadRequest);
        using var missing = await client.PostAsJsonAsync($"/api/candidates/{seed.Source}/merge", new { targetCandidateId = 999999 });
        await JsonAsync(missing, HttpStatusCode.NotFound);
        Assert.Equal(2, (await GetAsync(client, $"/api/candidates/{seed.Source}")).GetProperty("cvs").GetArrayLength());
    }

    [Fact]
    public async Task Concurrent_create_has_one_created_and_one_conflict_without_automatic_override()
    {
        var (factory, client, _) = await StartAsync(); using var f = factory; using var c = client;
        var responses = await Task.WhenAll(client.PostAsJsonAsync("/api/candidates", Request()), client.PostAsJsonAsync("/api/candidates", Request()));
        try { Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, responses.Select(r => r.StatusCode).Order()); }
        finally { foreach (var response in responses) response.Dispose(); }
        Assert.Equal(3, (await SnapshotAsync(f)).Candidates);
    }

    [Fact]
    public async Task Concurrent_attachments_to_same_CV_do_not_leave_an_orphan()
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        var responses = await Task.WhenAll(
            client.PutAsJsonAsync($"/api/cvs/{seed.Free}/candidate", new { fullName = "First Person", email = "first@example.com" }),
            client.PutAsJsonAsync($"/api/cvs/{seed.Free}/candidate", new { fullName = "Second Person", email = "second@example.com" }));
        try { foreach (var response in responses) await JsonAsync(response); }
        finally { foreach (var response in responses) response.Dispose(); }
        Assert.Equal(3, (await SnapshotAsync(f)).Candidates);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Disabled_HR_gate_has_no_candidate_routes(string environment)
    {
        using var factory = JobCrudEndpointTests.Factory(environment, false); using var client = factory.CreateClient();
        foreach (var path in new[] { "/api/candidates", "/api/candidates/1", $"/api/cvs/{Guid.NewGuid()}/candidate-suggestion" })
        {
            using var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var write = await client.PostAsJsonAsync("/api/candidates", Request()); Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
    }

    [Fact]
    public async Task CV_library_adds_phone_search_without_returning_anonymized_CVs()
    {
        var (factory, client, _) = await StartAsync(); using var f = factory; using var c = client;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            var free = await db.Cvs.SingleAsync(v => v.CandidateId == null && v.AnonymizedAt == null);
            free.ExtractedText = "No contact in this synthetic CV.";
            await db.SaveChangesAsync();
        }
        var page = await GetAsync(client, "/api/cvs?search=" + Uri.EscapeDataString("+84 912 345 678"));
        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
        Assert.All(page.GetProperty("items").EnumerateArray(), row => Assert.False(row.GetProperty("isAnonymized").GetBoolean()));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"fullName\":\"Test Name\",\"email\":\"x@y.com\",\"allowDuplicate\":\"true\"}")]
    [InlineData("{\"fullName\":\"Test Name\",\"email\":\"x@y.com\",\"Email\":\"other@y.com\"}")]
    public async Task Malformed_or_ambiguous_JSON_returns_safe_400(string body)
    {
        var (factory, client, _) = await StartAsync(); using var f = factory; using var c = client;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/candidates") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request);
        var error = await JsonAsync(response, HttpStatusCode.BadRequest);
        Assert.True(error.GetProperty("errors").TryGetProperty("body", out _));
        Assert.Equal(2, (await SnapshotAsync(f)).Candidates);
    }

    [Fact]
    public async Task Oversized_JSON_is_rejected_before_any_candidate_is_created()
    {
        var (factory, client, _) = await StartAsync(); using var f = factory; using var c = client;
        using var response = await client.PostAsJsonAsync("/api/candidates", new { fullName = new string('a', 17000), email = "x@y.com" });
        await JsonAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(2, (await SnapshotAsync(f)).Candidates);
    }

    [Fact]
    public async Task Missing_entities_return_404_and_reading_unextracted_CV_produces_empty_suggestions()
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        using var missing = await client.GetAsync("/api/candidates/999999"); await JsonAsync(missing, HttpStatusCode.NotFound);
        using var update = await client.PutAsJsonAsync("/api/candidates/999999", Request()); await JsonAsync(update, HttpStatusCode.NotFound);
        using var source = await client.PostAsJsonAsync("/api/candidates/999999/merge", new { targetCandidateId = seed.Target }); await JsonAsync(source, HttpStatusCode.NotFound);
        using var cv = await client.PutAsJsonAsync($"/api/cvs/{Guid.NewGuid()}/candidate", new { candidateId = seed.Target }); await JsonAsync(cv, HttpStatusCode.NotFound);
        using var target = await client.PutAsJsonAsync($"/api/cvs/{seed.Free}/candidate", new { candidateId = 999999 }); await JsonAsync(target, HttpStatusCode.NotFound);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            (await db.Cvs.SingleAsync(v => v.CvId == seed.Free)).ExtractedText = null;
            await db.SaveChangesAsync();
        }
        var suggestion = await GetAsync(client, $"/api/cvs/{seed.Free}/candidate-suggestion");
        foreach (var field in new[] { "fullName", "email", "phoneNumber" }) Assert.Equal(JsonValueKind.Null, suggestion.GetProperty(field).ValueKind);
        Assert.Empty(suggestion.GetProperty("matches").EnumerateArray());
    }

}
