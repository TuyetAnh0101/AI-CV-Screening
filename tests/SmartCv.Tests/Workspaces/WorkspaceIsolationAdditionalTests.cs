using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;

namespace SmartCv.Tests;

public class WorkspaceIsolationAdditionalTests
{
    [Fact]
    public async Task Deleted_candidate_journal_keeps_workspace_ownership_and_idempotent_erasure_status()
    {
        using var f = await WorkspaceIsolationFixture.CreateAsync();
        foreach (var w in new[] { 1, 2 }) await f.WithDb(w, async db =>
        {
            db.CandidateDataRequests.Add(new() { CandidateId = 900 + w, RequestType = "Erase", HandledBy = $"Synthetic workspace {w}", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });
        foreach (var w in new[] { 1, 2 })
        {
            using var client = f.Client(w);
            using var own = await client.PostAsJsonAsync($"/api/candidates/{900 + w}/erase", new { confirm = true });
            Assert.Equal(HttpStatusCode.Gone, own.StatusCode);
            using var foreign = await client.PostAsJsonAsync($"/api/candidates/{903 - w}/erase", new { confirm = true });
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
            var journal = await client.GetStringAsync("/api/data-requests");
            Assert.Contains($"Synthetic workspace {w}", journal); Assert.DoesNotContain($"Synthetic workspace {3 - w}", journal);
        }
    }
    [Fact]
    public async Task Attaching_new_candidate_stamps_principal_before_composite_foreign_key_fixup()
    {
        using var f = await WorkspaceIsolationFixture.CreateAsync(); using var client = f.Client(2);
        var before = await Snapshot(f, 1);
        using var response = await client.PutAsJsonAsync($"/api/cvs/{f.Data[2].Cv}/candidate", new { fullName = "New workspace B person", email = "new-b@example.invalid", confirmNew = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await f.WithDb(2, async db =>
        {
            var cv = await db.Cvs.Include(c => c.Candidate).SingleAsync(c => c.CvId == f.Data[2].Cv);
            Assert.Equal(2, cv.WorkspaceId); Assert.Equal(2, cv.Candidate!.WorkspaceId); Assert.Equal("new-b@example.invalid", cv.Candidate.Email);
        });
        Assert.Equal(before, await Snapshot(f, 1));
    }
    internal static readonly string[] ForeignWrites =
    [
        "DELETE /api/evaluation-sets/{id:int}/labels/{applicationId:int}",
        "DELETE /api/evaluation-sets/{id:int}/members/{applicationId:int}", "DELETE /api/jobs/{id:int}/skills/{code}",
        "PATCH /api/applications/{id:int}/stage", "POST /api/applications/{id:int}/flags",
        "POST /api/applications/flags/{flagId:int}/reopen", "POST /api/applications/flags/{flagId:int}/resolve",
        "POST /api/candidates/{id:int}/erase", "POST /api/candidates/{id:int}/merge",
        "POST /api/evaluation-sets/{id:int}/labels/import", "POST /api/evaluation-sets/{id:int}/members",
        "POST /api/jobs/{id:int}/skills", "PUT /api/candidates/{id:int}", "PUT /api/cvs/{id:guid}/candidate",
        "PUT /api/evaluation-sets/{id:int}/labels", "PUT /api/jobs/{id:int}",
        "PUT /api/jobs/{id:int}/skills/{code}", "PUT /api/jobs/{id:int}/skills/{code}/aliases"
    ];
    internal static readonly string[] Queries =
    ["GET /api/applications", "GET /api/dashboard/trend", "GET /api/data-requests", "GET /api/prompt-tests",
        "GET /api/prompts/active", "GET /api/runs/estimate", "GET /api/runs/latest"];
    internal static readonly string[] Constants =
    ["GET /api/applications/ai-preview/quota", "GET /api/applications/flag-reasons", "GET /api/applications/stage-options",
        "GET /api/exports/settings"]; // GET /api/retention/settings is System Admin only since #114 (A29AuthorizationTests.AdminRoutes)
    internal static readonly string[] Comparisons =
    ["GET /api/prompt-tests/{runId:int}", "GET /api/prompt-tests/compare", "GET /api/prompts/compare", "GET /api/runs/{id:int}/compare"];
    internal static readonly string[] BodyRoutes =
    ["POST /api/applications", "POST /api/applications/stage", "POST /api/evaluation-sets", "POST /api/prompt-tests",
        "POST /api/prompts", "POST /api/prompts/assembled-preview"];
    internal static IEnumerable<string> Covered => ForeignWrites.Concat(Queries).Concat(Constants).Concat(Comparisons).Concat(BodyRoutes);
    public static IEnumerable<object[]> Writes() => ForeignWrites.Select(r => new object[] { r });
    public static IEnumerable<object[]> QueryCases() => Queries.Select(r => new object[] { r });
    public static IEnumerable<object[]> ConstantCases() => Constants.Select(r => new object[] { r });
    public static IEnumerable<object[]> CompareCases() => Comparisons.Select(r => new object[] { r });
    public static IEnumerable<object[]> Bodies() => BodyRoutes.Select(r => new object[] { r });

    private static async Task<string> Snapshot(WorkspaceIsolationFixture f, int w)
    {
        string value = "";
        await f.WithDb(w, async db => value = JsonSerializer.Serialize(new
        {
            Jobs = await db.Jobs.AsNoTracking().OrderBy(x => x.JobId).ToListAsync(),
            Candidates = await db.Candidates.AsNoTracking().OrderBy(x => x.CandidateId).ToListAsync(),
            Cvs = await db.Cvs.AsNoTracking().OrderBy(x => x.CvId).ToListAsync(),
            Apps = await db.Applications.AsNoTracking().OrderBy(x => x.ApplicationId).ToListAsync(),
            Events = await db.ApplicationEvents.AsNoTracking().OrderBy(x => x.EventId).ToListAsync(),
            Flags = await db.ApplicationFlags.AsNoTracking().OrderBy(x => x.FlagId).ToListAsync(),
            Sets = await db.EvaluationSets.AsNoTracking().OrderBy(x => x.EvaluationSetId).ToListAsync(),
            Labels = await db.HrLabels.AsNoTracking().OrderBy(x => x.EvaluationSetId).ThenBy(x => x.ApplicationId).ToListAsync(),
            Members = await db.EvaluationSetMembers.AsNoTracking().OrderBy(x => x.EvaluationSetId).ThenBy(x => x.ApplicationId).ToListAsync()
        }));
        return value;
    }

    [Theory, MemberData(nameof(Writes))]
    public async Task Foreign_write_matches_missing_record_and_preserves_foreign_graph(string route)
    {
        using var f = await WorkspaceIsolationFixture.CreateAsync(); using var client = f.Client(2);
        var a = f.Data[1]; var b = f.Data[2];
        var before = await Snapshot(f, 1);
        var parts = route.Split(' ', 2);
        var id = route.Contains("/jobs/") ? a.Job : route.Contains("/candidates/") ? a.Candidate
            : route.Contains("/evaluation-sets/") ? a.Set : a.Application;
        var path = parts[1].Replace("{id:int}", id.ToString()).Replace("{id:guid}", a.Cv.ToString())
            .Replace("{flagId:int}", a.Flag.ToString()).Replace("{applicationId:int}", a.Application.ToString()).Replace("{code}", "a52s1");
        var absent = parts[1].Replace("{id:int}", "2147483647").Replace("{id:guid}", Guid.Empty.ToString())
            .Replace("{flagId:int}", "2147483647").Replace("{applicationId:int}", a.Application.ToString()).Replace("{code}", "a52s1");
        object payload = route.Contains("/aliases") ? new { aliases = new[] { "synthetic" } }
            : route.Contains("/skills") ? new { skillCode = "TEST", displayName = "Test", skillType = "MustHave" }
            : route.Contains("/jobs/") ? new { title = "Synthetic updated job" }
            : route.Contains("/candidate") && route.Contains("/cvs/") ? new { candidateId = b.Candidate }
            : route.EndsWith("/erase") ? new { confirm = true, note = "Synthetic erasure request" }
            : route.EndsWith("/merge") ? new { targetCandidateId = b.Candidate }
            : route.Contains("/candidates/") ? new { fullName = "Synthetic updated candidate" }
            : route.EndsWith("/members") ? new { applicationIds = new[] { b.Application } }
            : route.EndsWith("/labels") ? new { labels = new[] { new { applicationId = b.Application, isPass = true } } }
            : route.EndsWith("/stage") ? new { toStage = "Reviewing" }
            : new { reason = ManualFlagReasons.All[0], expectedVersion = Guid.NewGuid() };
        async Task<HttpResponseMessage> Send(string url)
        {
            using var request = new HttpRequestMessage(new(parts[0]), url);
            request.Content = route.EndsWith("/import")
                ? new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes($"ApplicationId,IsPass\n{b.Application},true\n")), "file", "labels.csv" } }
                : JsonContent.Create(payload);
            return await client.SendAsync(request);
        }
        using var foreign = await Send(path); using var missing = await Send(absent);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode); Assert.Equal(missing.StatusCode, foreign.StatusCode);
        var body = await foreign.Content.ReadAsStringAsync(); var missingBody = await missing.Content.ReadAsStringAsync();
        // Compare semantic errors; supplied IDs may be echoed, database values may not.
        if (body.Length > 0)
        {
            using var json = JsonDocument.Parse(body); using var other = JsonDocument.Parse(missingBody);
            Assert.Equal(other.RootElement.GetProperty("title").GetString(), json.RootElement.GetProperty("title").GetString());
        }
        Assert.DoesNotContain("A52 candidate 1", body); Assert.DoesNotContain("A52 job 1", body);
        Assert.Equal(before, await Snapshot(f, 1));
    }

    [Theory, MemberData(nameof(QueryCases))]
    public async Task Query_list_or_aggregate_uses_only_session_workspace(string route)
    {
        using var f = await WorkspaceIsolationFixture.CreateAsync();
        foreach (var w in new[] { 1, 2 })
        {
            using var client = f.Client(w); var mine = f.Data[w]; var other = f.Data[3 - w];
            var path = route.Split(' ', 2)[1];
            path += path == "/api/applications" ? $"?jobId={mine.Job}" : path == "/api/runs/estimate" ? "?count=2" : "";
            using var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync(); using var json = JsonDocument.Parse(text);
            Assert.DoesNotContain($"A52 job {3 - w}", text); Assert.DoesNotContain($"A52 prompt {3 - w}", text);
            Assert.DoesNotContain($"A52 candidate {3 - w}", text);
            switch (route)
            {
                case "GET /api/applications": Assert.Equal(2, json.RootElement.GetArrayLength()); break;
                case "GET /api/dashboard/trend": Assert.Equal(2, json.RootElement.GetProperty("totalApplications").GetInt32()); break;
                case "GET /api/data-requests": Assert.All(json.RootElement.EnumerateArray(), x => Assert.Equal(mine.Candidate, x.GetProperty("candidateId").GetInt32())); Assert.Equal(1, json.RootElement.GetArrayLength()); break;
                case "GET /api/prompts/active": Assert.Equal(mine.Prompt, json.RootElement.GetProperty("promptVersionId").GetInt32()); break;
                case "GET /api/runs/latest": Assert.Equal(mine.Run, json.RootElement.GetProperty("runId").GetInt32()); break;
                case "GET /api/runs/estimate": Assert.Equal(2, json.RootElement.GetProperty("count").GetInt32()); break;
                case "GET /api/prompt-tests": Assert.Equal(0, json.RootElement.GetArrayLength()); break; // fixture has Screening runs only
            }
        }
    }

    [Theory, MemberData(nameof(ConstantCases))]
    public async Task Reference_settings_return_no_workspace_records_for_either_session(string route)
    {
        using var f = await WorkspaceIsolationFixture.CreateAsync(); string? first = null;
        foreach (var w in new[] { 1, 2 })
        {
            using var client = f.Client(w); using var response = await client.GetAsync(route.Split(' ', 2)[1]);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(); using var json = JsonDocument.Parse(body);
            Assert.NotEqual("{}", body); Assert.DoesNotContain("A52", body); Assert.DoesNotContain("shared@example.invalid", body);
            if (!route.EndsWith("/quota")) { if (first is not null) Assert.Equal(first, body); first = body; }
        }
    }

    [Theory, MemberData(nameof(CompareCases))]
    public async Task Query_ids_cannot_read_foreign_prompt_or_run(string route)
    {
        using var f = await WorkspaceIsolationFixture.CreateAsync(); using var client = f.Client(2);
        var a = f.Data[1]; var b = f.Data[2];
        var path = route switch
        {
            "GET /api/prompt-tests/{runId:int}" => $"/api/prompt-tests/{a.Run}",
            "GET /api/prompt-tests/compare" => $"/api/prompt-tests/compare?baseRunId={a.Run}&candidateRunId={b.Run}",
            "GET /api/prompts/compare" => $"/api/prompts/compare?baseVersionId={a.Prompt}&candidateVersionId={b.Prompt}&evaluationSetId={b.Set}",
            _ => $"/api/runs/{a.Run}/compare?evaluationIds={a.Evaluation},{b.Evaluation}"
        };
        using var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("A52", body); Assert.DoesNotContain("shared@example.invalid", body);
    }

    [Theory, MemberData(nameof(Bodies))]
    public async Task Body_ids_bulk_and_new_records_preserve_workspace_boundary(string route)
    {
        using var f = await WorkspaceIsolationFixture.CreateAsync(); using var client = f.Client(2);
        var a = f.Data[1]; var b = f.Data[2]; var before = await Snapshot(f, 1);
        var path = route.Split(' ', 2)[1];
        object payload = path switch
        {
            "/api/applications" => new { jobId = b.Job, cvIds = new[] { a.Cv } },
            "/api/applications/stage" => new { applicationIds = new[] { a.Application, b.Application }, toStage = "Reviewing" },
            "/api/evaluation-sets" => new { name = "Synthetic rejected set", jobId = a.Job },
            "/api/prompt-tests" => new { evaluationSetId = a.Set, promptVersionId = b.Prompt },
            "/api/prompts/assembled-preview" => new { applicationId = a.Application, promptVersionId = b.Prompt },
            _ => new { versionName = "Synthetic draft B", systemPrompt = "Workspace B synthetic system prompt" }
        };
        using var response = await client.PostAsJsonAsync(path, payload);
        var body = await response.Content.ReadAsStringAsync();
        var expected = path == "/api/evaluation-sets" ? HttpStatusCode.BadRequest
            : path is "/api/prompt-tests" or "/api/prompts/assembled-preview" ? HttpStatusCode.NotFound
            : path == "/api/prompts" ? HttpStatusCode.Created : HttpStatusCode.OK;
        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain("A52 candidate 1", body); Assert.DoesNotContain("A52 job 1", body);
        using var json = JsonDocument.Parse(body);
        if (path == "/api/applications") Assert.Contains("CvNotFound", body);
        if (path == "/api/applications/stage")
        {
            Assert.Equal(1, json.RootElement.GetProperty("changed").GetInt32()); Assert.Equal(1, json.RootElement.GetProperty("notFound").GetInt32());
            var results = json.RootElement.GetProperty("results").EnumerateArray().ToArray();
            Assert.Equal("NotFound", results.Single(x => x.GetProperty("applicationId").GetInt32() == a.Application).GetProperty("outcome").GetString());
            await f.WithDb(2, async db => Assert.Equal("Reviewing", (await db.Applications.SingleAsync(x => x.ApplicationId == b.Application)).Stage));
        }
        if (path == "/api/prompts") await f.WithDb(2, async db => Assert.Equal(2, await db.PromptVersions.CountAsync()));
        Assert.Equal(before, await Snapshot(f, 1));
    }
}
