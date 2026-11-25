using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SmartCv.Core.Contracts;
using SmartCv.Core.Uploads;
using SmartCv.Core.Workspaces;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.E2E;

/// <summary>B57 E3/E4/E5/E8/E10/R5/R11/§12: two Personal Owners plus internal HR, real JWT/session validation.</summary>
public sealed class B57IsolationSessionTests
{
    /// <summary>Business routes whose workspace isolation is proven here and in B57ReportScopeTests (used by WorkspaceRouteCoverageTests).</summary>
    internal static readonly string[] Covered =
    [
        "GET /api/hr/reports/summary", "GET /api/hr/reports/rows", "GET /api/hr/reports/preview", "GET /api/hr/reports/export",
        "GET /api/hr/reports/export/pdf", "GET /api/workspace", "POST /api/workspace/sample-job"
    ];

    [Fact]
    public async Task Workspace_endpoint_shows_only_the_session_workspace_and_sample_job_is_owner_only()
    {
        using var world = await B57World.CreateAsync();
        using var a = world.Business(B57World.OwnerAWorkspace);
        using var hr = world.Business(B57World.Internal);
        var mine = await a.GetStringAsync("/api/workspace");
        Assert.Contains("Không gian B57 A", mine); Assert.DoesNotContain("Không gian B57 B", mine); Assert.DoesNotContain(world.OwnerB.Email, mine);
        Assert.DoesNotContain("Không gian B57", await hr.GetStringAsync("/api/workspace"));
        using (var denied = await hr.PostAsync("/api/workspace/sample-job", null))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var sample = await a.PostAsync("/api/workspace/sample-job", null))
            Assert.True(sample.IsSuccessStatusCode, ((int)sample.StatusCode) + " " + await sample.Content.ReadAsStringAsync());
        await world.WithDb(B57World.OwnerAWorkspace, async db => Assert.Equal(2, await db.Jobs.CountAsync()));
        await world.WithDb(B57World.OwnerBWorkspace, async db => Assert.Equal(1, await db.Jobs.CountAsync()));
        await world.WithDb(B57World.Internal, async db => Assert.Equal(1, await db.Jobs.CountAsync()));
    }
    private static string[] DetailPaths(B57World.Graph g) =>
    [
        $"/api/jobs/{g.Job}", $"/api/candidates/{g.Candidates[0]}", $"/api/candidates/{g.Candidates[0]}/data-export",
        $"/api/cvs/{g.Cvs[0]}", $"/api/cvs/{g.Cvs[0]}/file", $"/api/cvs/{g.Cvs[0]}/text", $"/api/cvs/{g.Cvs[0]}/candidate-suggestion",
        $"/api/runs/{g.Run}", $"/api/runs/{g.Run}/ranking", $"/api/runs/{g.Run}/failures", $"/api/runs/{g.Run}/ranking/export",
        $"/api/runs/{g.Run}/ranking/export/preview", $"/api/evaluations/{g.Evaluations[0]}", $"/api/prompts/{g.Prompt}",
        $"/api/evaluation-sets/{g.Set}", $"/api/evaluation-sets/{g.Set}/applications", $"/api/applications/{g.Applications[0]}/events",
        $"/api/applications/{g.Applications[0]}/flags", $"/api/applications/{g.Applications[0]}/keyword-score",
        $"/api/prompt-tests/{g.Run}", $"/api/hr/reports/summary?jobId={g.Job}", $"/api/hr/reports/rows?runId={g.Run}"
    ];

    [Theory]
    [InlineData(B57World.OwnerAWorkspace, B57World.OwnerBWorkspace)]
    [InlineData(B57World.OwnerBWorkspace, B57World.OwnerAWorkspace)]
    [InlineData(B57World.OwnerAWorkspace, B57World.Internal)]
    [InlineData(B57World.Internal, B57World.OwnerAWorkspace)]
    public async Task E3_R5_direct_URLs_to_another_workspace_are_404_while_own_URLs_are_200(int viewer, int owner)
    {
        using var world = await B57World.CreateAsync();
        using var http = world.Business(viewer);
        var foreign = DetailPaths(world.Data[owner]);
        var own = DetailPaths(world.Data[viewer]);
        for (var i = 0; i < foreign.Length; i++)
        {
            using var mine = await http.GetAsync(own[i]);
            // A synthetic CV has no stored file; /file is a 404 for the owner too (same body), never 200 with foreign bytes.
            if (!own[i].EndsWith("/file") && !own[i].StartsWith("/api/prompt-tests/"))
                Assert.True(mine.StatusCode == HttpStatusCode.OK, $"own {own[i]}: {(int)mine.StatusCode}");
            using var response = await http.GetAsync(foreign[i]);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"foreign {foreign[i]}: {(int)response.StatusCode}");
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(B57World.Marker(owner), body);
            Assert.DoesNotContain("@example.invalid", body);
        }
    }

    [Theory]
    [InlineData(B57World.Internal)]
    [InlineData(B57World.OwnerAWorkspace)]
    [InlineData(B57World.OwnerBWorkspace)]
    public async Task E4_lists_search_paging_dashboard_and_counts_show_only_the_session_workspace(int workspace)
    {
        using var world = await B57World.CreateAsync();
        using var http = world.Business(workspace);
        var marker = B57World.Marker(workspace);
        foreach (var path in new[]
                 {
                     "/api/jobs", "/api/cvs", "/api/cvs?search=B57&page=1&pageSize=1", "/api/candidates", "/api/candidates?search=B57_OWNER",
                     "/api/runs", "/api/prompts", "/api/evaluation-sets", "/api/applications?jobId=" + world.Data[workspace].Job,
                     "/api/dashboard/trend", "/api/runs/latest", "/api/prompts/active", "/api/hr/reports/rows?pageSize=100"
                 })
        {
            using var response = await http.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {body[..Math.Min(body.Length, 700)]}");
            foreach (var foreign in B57World.ForeignMarkers(workspace)) Assert.DoesNotContain(foreign, body);
            foreach (var w in B57World.Workspaces.Where(w => w != workspace))
                Assert.DoesNotContain($"b57-{w}-0@example.invalid", body);
        }
        // /api/dashboard/summary uses SQL Server-only functions: counted per workspace in B57SqlServerTests.
        using var trend = JsonDocument.Parse(await http.GetStringAsync("/api/dashboard/trend"));
        Assert.Equal(2, trend.RootElement.GetProperty("totalApplications").GetInt32());
        using var cvs = JsonDocument.Parse(await http.GetStringAsync("/api/cvs?page=1&pageSize=1"));
        Assert.Equal(2, cvs.RootElement.GetProperty("totalCount").GetInt32());
        var jobs = await http.GetStringAsync("/api/jobs");
        Assert.Contains(marker, jobs);
    }

    [Fact]
    public async Task E5_identical_CV_bytes_in_two_personal_workspaces_never_reveal_the_other_upload()
    {
        using var world = await B57World.CreateAsync();
        var bytes = TestSupport.Pdf("B57 synthetic CV with identical bytes in two workspaces " + Guid.NewGuid());
        async Task<CvUploadItemResult> Upload(HttpClient http, string name)
        {
            using var content = new MultipartFormDataContent
            {
                { new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } }, "files", name },
                { new StringContent("true"), "consentConfirmed" }, { new StringContent("Email"), "sourceChannel" }
            };
            using var response = await http.PostAsync("/api/cvs/upload", content);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            return Assert.Single(JsonSerializer.Deserialize<CvUploadResponse>(body, JsonSerializerOptions.Web)!.Files);
        }
        using var a = world.Business(B57World.OwnerAWorkspace);
        using var b = world.Business(B57World.OwnerBWorkspace);
        var first = await Upload(a, "B57_OWNER_A_PRIVATE-upload.pdf");
        Assert.Equal(CvUploadStatuses.Accepted, first.Status);
        var other = await Upload(b, "b-upload.pdf");
        Assert.Equal(CvUploadStatuses.Accepted, other.Status);
        Assert.Null(other.ExistingCvId); Assert.NotEqual(first.CvId, other.CvId);
        Assert.DoesNotContain("B57_OWNER_A_PRIVATE", JsonSerializer.Serialize(other));
        var again = await Upload(b, "b-upload-again.pdf");
        Assert.Equal(CvUploadErrorCodes.Duplicate, again.ErrorCode);
        Assert.Equal(other.CvId, again.ExistingCvId); // duplicate is detected within B only
        using var foreign = await b.GetAsync($"/api/cvs/{first.CvId}/file");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var own = await a.GetAsync($"/api/cvs/{first.CvId}/file");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(bytes, await own.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task E8_R11_Admin_lock_revokes_the_old_token_and_blocks_login_then_unlock_requires_a_new_session()
    {
        using var world = await B57World.CreateAsync();
        using var owner = world.Business(B57World.OwnerAWorkspace);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/hr/reports/summary")).StatusCode);
        using var admin = world.AdminClient();
        var users = await admin.GetStringAsync("/api/users");
        Assert.Contains(world.OwnerA.Email, users);
        Assert.DoesNotContain("passwordHash", users, StringComparison.OrdinalIgnoreCase);
        using (var locked = await admin.PostAsync($"/api/users/{world.OwnerA.UserId}/lock", null))
            Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        foreach (var path in new[] { "/api/hr/reports/summary", "/api/hr/reports/export", $"/api/cvs/{world.Data[2].Cvs[0]}/file", "/api/auth/me" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync(path)).StatusCode);
        using var anonymous = world.Anonymous();
        using (var login = await anonymous.PostAsJsonAsync("/api/auth/login", new { email = world.OwnerA.Email, password = world.Password }))
            Assert.True(login.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest, ((int)login.StatusCode).ToString());
        using (var unlocked = await admin.PostAsync($"/api/users/{world.OwnerA.UserId}/unlock", null))
            Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/hr/reports/summary")).StatusCode); // old stamp stays revoked
        using var fresh = await anonymous.PostAsJsonAsync("/api/auth/login", new { email = world.OwnerA.Email, password = world.Password });
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        var token = (await fresh.Content.ReadFromJsonAsync<LoginResponse>())!;
        Assert.Equal(B57World.OwnerAWorkspace, token.User.Workspace!.Id);
        using var renewed = world.Anonymous();
        renewed.DefaultRequestHeaders.Authorization = new("Bearer", token.Token);
        var report = await renewed.GetStringAsync("/api/hr/reports/rows");
        Assert.Contains("HS-", report); Assert.DoesNotContain("B57_OWNER_B_PRIVATE", report);
        await world.WithGlobalDb(async db => Assert.Contains(db.AuditEvents, e => e.ActionCode.Contains("lock") && e.ActorUserId == world.Admin.UserId));
    }

    [Fact]
    public async Task R11_lost_membership_deactivated_workspace_role_change_and_ownership_change_deny_new_requests()
    {
        using var world = await B57World.CreateAsync();
        using var a = world.Business(B57World.OwnerAWorkspace);
        using var b = world.Business(B57World.OwnerBWorkspace);
        using var hr = world.Business(B57World.Internal);
        using var empty = world.Business(B57World.EmptyWorkspace);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/jobs")).StatusCode);
        await world.WithGlobalDb(async db =>
        {
            db.WorkspaceMembers.Remove(await db.WorkspaceMembers.SingleAsync(m => m.UserId == world.OwnerA.UserId));
            (await db.Workspaces.SingleAsync(w => w.WorkspaceId == B57World.OwnerBWorkspace)).IsActive = false;
            // Ownership moved away: the remaining "Owner" membership row alone must not grant Personal business access.
            (await db.Workspaces.SingleAsync(w => w.WorkspaceId == B57World.EmptyWorkspace)).OwnerUserId = world.Admin.UserId;
            (await db.Users.SingleAsync(u => u.UserId == world.InternalHr.UserId)).Role = "Admin"; // stamp not rotated on purpose
            await db.SaveChangesAsync();
        });
        foreach (var client in new[] { a, b, hr })
            foreach (var path in new[] { "/api/jobs", "/api/hr/reports/export", $"/api/runs/{world.Data[2].Run}/ranking/export" })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.DoesNotContain("B57_", await response.Content.ReadAsStringAsync());
            }
        using (var ownerless = await empty.GetAsync("/api/hr/reports/summary"))
            Assert.True(ownerless.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, ((int)ownerless.StatusCode).ToString());
    }

    [Fact]
    public async Task Security_expired_forged_and_claim_tampered_tokens_fail_closed()
    {
        using var world = await B57World.CreateAsync();
        var clock = new A28TestSupport.Clock(DateTimeOffset.UtcNow.AddDays(-2));
        var expired = A28TestSupport.Token(world.OwnerA, clock, workspaceId: B57World.OwnerAWorkspace);
        var forged = A28TestSupport.Token(world.OwnerA, settings: A28TestSupport.Settings(new string('f', 96)), workspaceId: B57World.OwnerAWorkspace);
        // Valid signature, but the client asks for Owner B's workspace: membership is checked in the database.
        var otherWorkspace = A28TestSupport.Token(world.OwnerA, workspaceId: B57World.OwnerBWorkspace);
        var unsigned = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(new JwtHeader(), new JwtPayload(
            new JwtSecurityTokenHandler().ReadJwtToken(A28TestSupport.Token(world.OwnerA, workspaceId: B57World.OwnerBWorkspace)).Claims)));
        foreach (var token in new[] { expired, forged, otherWorkspace, unsigned, "not-a-jwt", expired + "x" })
        {
            using var http = world.Anonymous();
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
            foreach (var path in new[] { "/api/hr/reports/summary", "/api/jobs", $"/api/cvs/{world.Data[3].Cvs[0]}/file" })
            {
                using var response = await http.GetAsync(path);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.DoesNotContain("B57_", await response.Content.ReadAsStringAsync());
            }
        }
        // Personal workspace claim without ANY membership (unverified/unaffiliated account) cannot fall back to a default workspace.
        using var orphan = world.Client(world.Unverified, B57World.Internal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await orphan.GetAsync("/api/jobs")).StatusCode);
        using var noClaim = world.Client(world.Unverified, null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await noClaim.GetAsync("/api/jobs")).StatusCode);
    }

    [Fact]
    public async Task E10_daily_AI_quota_of_3_rejects_overflow_without_creating_runs_or_ledger_rows()
    {
        using var world = await B57World.CreateAsync();
        await world.WithGlobalDb(async db =>
        {
            (await db.Workspaces.SingleAsync(w => w.WorkspaceId == B57World.OwnerAWorkspace)).DailyAiEvaluationLimit = 3;
            await db.SaveChangesAsync();
        });
        var apps = world.Data[B57World.OwnerAWorkspace].Applications;
        using var a = world.Business(B57World.OwnerAWorkspace);
        using (var first = await a.PostAsJsonAsync("/api/runs", new { applicationIds = apps }))
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);   // 2 of 3
        using (var overflow = await a.PostAsJsonAsync("/api/runs", new { applicationIds = apps }))
        {
            Assert.Equal((HttpStatusCode)429, overflow.StatusCode);      // 2 more would be 4 > 3
            var body = await overflow.Content.ReadAsStringAsync();
            Assert.Contains("ai_quota_exceeded", body); Assert.DoesNotContain("B57_", body);
        }
        using (var last = await a.PostAsJsonAsync("/api/runs", new { applicationIds = new[] { apps[0] } }))
            Assert.Equal(HttpStatusCode.Created, last.StatusCode);    // exactly 3
        using (var none = await a.PostAsJsonAsync("/api/runs", new { applicationIds = new[] { apps[1] } }))
            Assert.Equal((HttpStatusCode)429, none.StatusCode);
        await world.WithDb(B57World.OwnerAWorkspace, async db =>
        {
            Assert.Equal(3, await db.EvaluationRuns.CountAsync());         // seeded + 2 accepted runs
            Assert.Equal(3, await db.AiUsageEntries.SumAsync(e => e.Units - e.UnitsReleased));
        });
        // Another workspace's quota is independent (Owner B has no limit set).
        using var b = world.Business(B57World.OwnerBWorkspace);
        using (var other = await b.PostAsJsonAsync("/api/runs", new { applicationIds = world.Data[3].Applications }))
            Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    [Fact]
    public async Task Parallel_sessions_of_two_owners_never_mix_results()
    {
        using var world = await B57World.CreateAsync();
        using var a = world.Business(B57World.OwnerAWorkspace);
        using var b = world.Business(B57World.OwnerBWorkspace);
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            var (client, mine, other) = i % 2 == 0 ? (a, "B57_OWNER_A_PRIVATE", "B57_OWNER_B_PRIVATE") : (b, "B57_OWNER_B_PRIVATE", "B57_OWNER_A_PRIVATE");
            var body = await client.GetStringAsync(i % 4 < 2 ? "/api/hr/reports/rows?anonymous=false" : "/api/candidates");
            Assert.Contains(mine, body); Assert.DoesNotContain(other, body);
        });
        await Task.WhenAll(tasks);
    }
}
