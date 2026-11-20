using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Api.Auditing;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Auth;

public class B43AuditTests
{
    internal static WebApplicationFactory<Program> Factory() => A28TestSupport.Factory(new()
        { ["Scoring:WorkerEnabled"] = "false", ["Retention:AutoAnonymize"] = "false" });
    internal static async Task<List<AuditEvent>> Events(WebApplicationFactory<Program> factory)
    {
        List<AuditEvent> rows = [];
        await A28TestSupport.WithDb(factory, async db => rows = await db.AuditEvents.AsNoTracking().OrderBy(e => e.AuditEventId).ToListAsync());
        return rows;
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("HR")]
    public async Task Login_records_verified_identity_UTC_and_no_credentials_or_entered_email(string role)
    {
        using var factory = Factory();
        var password = A28TestSupport.Password(); var user = await A28TestSupport.Seed(factory, password, role);
        using var http = factory.CreateClient();
        using var response = await http.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
        var row = Assert.Single(await Events(factory));
        Assert.Equal(("auth.login", "Succeeded", user.UserId, role), (row.ActionCode, row.Outcome, row.ActorUserId, row.ActorRoleSnapshot));
        Assert.Equal(user.FullName, row.ActorNameSnapshot); Assert.InRange(row.OccurredAtUtc, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.NotEmpty(row.RequestId);
        var json = JsonSerializer.Serialize(row);
        foreach (var secret in new[] { password, user.Email, user.PasswordHash, user.SecurityStamp, token }) Assert.DoesNotContain(secret, json);
    }

    [Theory]
    [InlineData("missing", 401, "invalid_credentials")]
    [InlineData("wrong", 401, "invalid_credentials")]
    [InlineData("locked", 403, "account_locked")]
    public async Task Login_denials_have_normalized_reason_without_unverified_actor_or_email(string kind, int status, string reason)
    {
        using var factory = Factory(); var password = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(factory, password, "HR", active: kind != "locked");
        var email = kind == "missing" ? Guid.NewGuid().ToString("N") + "@example.invalid" : user.Email;
        var supplied = kind == "wrong" ? A28TestSupport.Password() : password;
        using var http = factory.CreateClient(); using var response = await http.PostAsJsonAsync("/api/auth/login", new { email, password = supplied });
        Assert.Equal(status, (int)response.StatusCode);
        var row = Assert.Single(await Events(factory)); Assert.Equal(("Denied", reason), (row.Outcome, row.FailureCode));
        Assert.Null(row.ActorUserId); Assert.Null(row.ActorNameSnapshot); Assert.Null(row.ActorRoleSnapshot);
        var json = JsonSerializer.Serialize(row); Assert.DoesNotContain(email, json); Assert.DoesNotContain(supplied, json);
    }

    [Fact]
    public async Task Accounts_and_password_flow_have_trusted_actors_and_revoke_old_JWT_without_mutating_the_original_Admin()
    {
        using var factory = Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        var fingerprint = A30AccountEndpointTests.Fingerprint(await A30AccountEndpointTests.Saved(factory, admin.UserId));
        using var manager = A29AuthorizationTests.Client(factory, admin);
        var created = await A30AccountEndpointTests.Created(manager);
        using var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = created.User.Email, password = created.TemporaryPassword });
        var temporary = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        using var hr = factory.CreateClient(); hr.DefaultRequestHeaders.Authorization = new("Bearer", temporary.Token);
        var newPassword = A28TestSupport.Password();
        using var change = await hr.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = created.TemporaryPassword, newPassword });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await hr.GetAsync("/api/auth/me")).StatusCode);
        using var locked = await manager.PostAsync($"/api/users/{created.User.UserId}/lock", null); Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        using var unlocked = await manager.PostAsync($"/api/users/{created.User.UserId}/unlock", null); Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        using var reset = await manager.PostAsync($"/api/users/{created.User.UserId}/reset-password", null); Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var resetBody = (await reset.Content.ReadFromJsonAsync<UserCredentialResponse>())!;
        var rows = await Events(factory);
        foreach (var action in new[] { "accounts.created", "accounts.locked", "accounts.unlocked", "accounts.password_reset" })
            Assert.Equal(admin.UserId, Assert.Single(rows, e => e.ActionCode == action).ActorUserId);
        Assert.Equal(created.User.UserId, Assert.Single(rows, e => e.ActionCode == "auth.password_changed").ActorUserId);
        Assert.Contains(rows, e => e.ActionCode == "auth.session_rejected" && e.ActorUserId is null);
        var json = JsonSerializer.Serialize(rows);
        foreach (var secret in new[] { created.TemporaryPassword!, newPassword, resetBody.TemporaryPassword!, temporary.Token }) Assert.DoesNotContain(secret, json);
        Assert.True(fingerprint == A30AccountEndpointTests.Fingerprint(await A30AccountEndpointTests.Saved(factory, admin.UserId)));
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("HR", 403)]
    [InlineData("Admin", 200)]
    [InlineData("mcp", 403)]
    [InlineData("locked", 401)]
    public async Task Audit_API_enforces_current_Admin_privileges(string who, int status)
    {
        using var factory = Factory();
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), who == "HR" ? "HR" : "Admin", mustChange: who == "mcp");
        using var http = who == "anonymous" ? factory.CreateClient() : A29AuthorizationTests.Client(factory, user);
        if (who == "locked") await A28TestSupport.WithDb(factory, async db => { (await db.Users.SingleAsync()).IsActive = false; await db.SaveChangesAsync(); });
        using var response = await http.GetAsync("/api/admin/audit-events"); Assert.Equal(status, (int)response.StatusCode);
    }

    [Fact]
    public async Task Audit_queries_are_paginated_filtered_stable_and_do_not_audit_themselves()
    {
        using var factory = Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        var now = DateTime.UtcNow;
        await A28TestSupport.WithDb(factory, async db =>
        {
            db.AuditEvents.AddRange(Enumerable.Range(1, 7).Select(i => new AuditEvent { OccurredAtUtc = now,
                ActorUserId = admin.UserId, ActorNameSnapshot = admin.FullName, ActorRoleSnapshot = "Admin",
                ActionCode = i <= 4 ? "auth.login" : "accounts.updated", Category = i <= 4 ? "Authentication" : "Accounts",
                ResourceType = "User", ResourceId = i.ToString(), Outcome = i % 2 == 0 ? "Denied" : "Succeeded", RequestId = "request-" + i, SafeSummary = "Synthetic safe action" }));
            await db.SaveChangesAsync();
        });
        using var http = A29AuthorizationTests.Client(factory, admin);
        var first = (await http.GetFromJsonAsync<AuditPageDto>("/api/admin/audit-events?pageSize=2"))!;
        var second = (await http.GetFromJsonAsync<AuditPageDto>("/api/admin/audit-events?pageSize=2&page=2"))!;
        Assert.Equal((7, 4, 3, 3), (first.TotalCount, first.LoginCount, first.AccountActionCount, first.FailureCount));
        Assert.Equal(2, first.Items.Count); Assert.True(first.Items[0].AuditEventId > first.Items[1].AuditEventId);
        Assert.Empty(first.Items.Select(e => e.AuditEventId).Intersect(second.Items.Select(e => e.AuditEventId)));
        var filtered = (await http.GetFromJsonAsync<AuditPageDto>($"/api/admin/audit-events?actorUserId={admin.UserId}&outcome=Denied&category=Authentication"))!;
        Assert.Equal(2, filtered.TotalCount);
        var detail = await http.GetFromJsonAsync<AuditEventDto>($"/api/admin/audit-events/{first.Items[0].AuditEventId}"); Assert.Equal("request-7", detail!.RequestId);
        var empty = (await http.GetFromJsonAsync<AuditPageDto>("/api/admin/audit-events?requestId=not-found"))!; Assert.Empty(empty.Items); Assert.Equal(0, empty.TotalCount);
        Assert.Equal(7, (await Events(factory)).Count);
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        { using var request = new HttpRequestMessage(method, "/api/admin/audit-events/1"); using var denied = await http.SendAsync(request); Assert.InRange((int)denied.StatusCode, 400, 499); }
        Assert.Equal(7, (await Events(factory)).Count);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=101")]
    [InlineData("from=2026-01-01&to=2026-12-31")]
    [InlineData("actorUserId=1&actorUserId=2")]
    [InlineData("outcome=unknown")]
    public async Task Invalid_audit_filters_are_safe_400(string query)
    {
        using var factory = Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        using var http = A29AuthorizationTests.Client(factory, admin); using var response = await http.GetAsync("/api/admin/audit-events?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Contains("audit_invalid_filter", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task All_instrumented_routes_exist_and_runs_remains_available_to_HR()
    {
        using var factory = Factory(); var hr = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        using var http = A29AuthorizationTests.Client(factory, hr);
        var actual = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>() is not null)
            .SelectMany(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Select(m => m + " /" + e.RoutePattern.RawText!.Trim('/'))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var route in AuditCatalog.Routes.Keys) Assert.Contains(route, actual);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/runs")).StatusCode);
    }

    internal static async Task<Guid> Upload(HttpClient http, byte[] bytes, string name = "Hồ sơ tổng hợp.pdf")
    {
        using var body = new MultipartFormDataContent(); body.Add(new StringContent("Email"), "sourceChannel"); body.Add(new StringContent("true"), "consentConfirmed");
        body.Add(new ByteArrayContent(bytes), "files", name);
        using var response = await http.PostAsync("/api/cvs/upload", body); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CvUploadResponse>())!.Files.Single().CvId!.Value;
    }

    [Fact]
    public async Task Job_prompt_upload_stage_and_scoring_events_come_from_committed_handlers_without_private_payloads()
    {
        using var factory = Factory(); var user = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        using var http = A29AuthorizationTests.Client(factory, user);
        var request = Jobs.JobRulesTests.Valid("Synthetic audit job " + Guid.NewGuid().ToString("N"));
        request.ClosingDate = SmartCv.Core.Common.VietnamTime.Today().AddDays(7);
        using var createdJob = await http.PostAsJsonAsync("/api/jobs", request); Assert.Equal(HttpStatusCode.Created, createdJob.StatusCode);
        var job = (await createdJob.Content.ReadFromJsonAsync<JobDto>())!;
        request.Title += " updated";
        Assert.Equal(HttpStatusCode.OK, (await http.PutAsJsonAsync($"/api/jobs/{job.JobId}", request)).StatusCode);
        var promptSecret = "Synthetic private prompt " + Guid.NewGuid().ToString("N");
        using var createdPrompt = await http.PostAsJsonAsync("/api/prompts", new CreatePromptVersionRequest { VersionName = Guid.NewGuid().ToString("N"), SystemPrompt = promptSecret });
        Assert.Equal(HttpStatusCode.Created, createdPrompt.StatusCode);
        var prompt = (await createdPrompt.Content.ReadFromJsonAsync<PromptVersionDto>())!;
        // Existing activation uses relational transactions/ExecuteUpdate. InMemory must log its failure, never a success.
        Assert.Equal(HttpStatusCode.InternalServerError, (await http.PostAsJsonAsync($"/api/prompts/{prompt.PromptVersionId}/activate", new { })).StatusCode);
        await A28TestSupport.WithDb(factory, async db =>
        {
            foreach (var version in await db.PromptVersions.ToListAsync()) version.IsActive = version.PromptVersionId == prompt.PromptVersionId;
            await db.SaveChangesAsync(); // Fixture prerequisite for the independent scoring request below.
        });
        var cv = await Upload(http, TestSupport.Pdf(Guid.NewGuid().ToString("N")));
        var application = Applications.ApplicationStageEndpointTests.NewApplication();
        await A28TestSupport.WithDb(factory, async db => { db.Add(application); await db.SaveChangesAsync(); });
        using var decision = await http.PatchAsJsonAsync($"/api/applications/{application.ApplicationId}/stage", new ChangeStageRequest
            { ToStage = "Reviewing", Actor = "forged administrator", ExpectedStageVersion = application.StageVersion });
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        using var run = await http.PostAsJsonAsync("/api/runs", new StartRunRequest { ApplicationIds = [application.ApplicationId] });
        Assert.Equal(HttpStatusCode.Created, run.StatusCode);
        using var invalid = await http.PostAsJsonAsync("/api/jobs", new { title = "" }); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var rows = await Events(factory);
        foreach (var code in new[] { "jobs.created", "jobs.updated", "prompts.created", "cvs.upload", "applications.stage_changed", "runs.started" })
            Assert.Contains(rows, e => e.ActionCode == code && e.Outcome == "Succeeded" && e.ActorUserId == user.UserId);
        Assert.Contains(rows, e => e.ActionCode == "prompts.activated" && e.Outcome == "Failed");
        Assert.DoesNotContain(rows, e => e.ActionCode == "prompts.activated" && e.Outcome == "Succeeded");
        Assert.Contains(rows, e => e.ActionCode == "jobs.created" && e.Outcome == "Denied");
        var json = JsonSerializer.Serialize(rows); Assert.DoesNotContain(promptSecret, json); Assert.DoesNotContain(request.Title, json); Assert.DoesNotContain("forged administrator", json);
        await A28TestSupport.WithDb(factory, async db => Assert.Equal(user.UserId, (await db.ApplicationEvents.SingleAsync()).UserId));
    }

    [Fact]
    public async Task Simultaneous_requests_never_share_an_actor_snapshot()
    {
        using var factory = Factory();
        var first = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR"); var second = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        using var a = A29AuthorizationTests.Client(factory, first); using var b = A29AuthorizationTests.Client(factory, second);
        var responses = await Task.WhenAll(a.PostAsJsonAsync("/api/candidates", new { fullName = "Synthetic first", email = Guid.NewGuid().ToString("N") + "@example.invalid" }),
            b.PostAsJsonAsync("/api/candidates", new { fullName = "Synthetic second", email = Guid.NewGuid().ToString("N") + "@example.invalid" }));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var rows = (await Events(factory)).Where(e => e.ActionCode == "candidates.created").ToList(); Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { first.UserId, second.UserId }.Order(), rows.Select(e => e.ActorUserId!.Value).Order());
        Assert.Equal(2, rows.Select(e => e.RequestId).Distinct().Count());
        foreach (var response in responses) response.Dispose();
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("HR")]
    public async Task File_access_is_authenticated_private_and_audited_as_opened_not_download_completed(string role)
    {
        using var factory = Factory(); var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), role);
        using var http = A29AuthorizationTests.Client(factory, user);
        var bytes = TestSupport.Pdf(Guid.NewGuid().ToString("N")); var id = await Upload(http, bytes);
        using var anonymous = factory.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/cvs/{id}/file")).StatusCode);
        using var file = await http.GetAsync($"/api/cvs/{id}/file?download=true"); Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.True(file.Headers.CacheControl!.NoStore); Assert.Equal("application/pdf", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal("Hồ sơ tổng hợp.pdf", file.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Equal(bytes, await file.Content.ReadAsByteArrayAsync());
        var row = Assert.Single(await Events(factory), e => e.ActionCode == "cvs.file_access_granted" && e.Outcome == "AccessGranted");
        Assert.Equal((user.UserId, id.ToString()), (row.ActorUserId, row.ResourceId)); Assert.Contains("mở stream", row.SafeSummary);
        await A28TestSupport.WithDb(factory, async db => { (await db.Cvs.SingleAsync(c => c.CvId == id)).AnonymizedAt = DateTime.UtcNow; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Gone, (await http.GetAsync($"/api/cvs/{id}/file")).StatusCode);
        Assert.Single(await Events(factory), e => e.ActionCode == "cvs.file_access_granted" && e.Outcome == "AccessGranted");
    }

    [Fact]
    public async Task Failure_to_store_file_audit_prevents_stream_delivery_without_sensitive_errors()
    {
        var fault = new A28TestSupport.DataFault(); using var factory = A28TestSupport.Factory(fault: fault);
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password()); using var http = A29AuthorizationTests.Client(factory, user);
        var id = await Upload(http, TestSupport.Pdf(Guid.NewGuid().ToString("N"))); fault.FailSave = true;
        using var response = await http.GetAsync($"/api/cvs/{id}/file"); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(); Assert.Contains("audit_unavailable", text); Assert.DoesNotContain("SYNTHETIC_PRIVATE_SQL_ERROR", text); Assert.DoesNotContain("%PDF", text);
    }
}
