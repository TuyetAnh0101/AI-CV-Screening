using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;

namespace SmartCv.Tests.Auth;

/// <summary>Real JwtBearer + real endpoints, isolated InMemory and synthetic accounts/files only.</summary>
public class HrAdminBusinessPermissionsTests
{
    // A failed integrity assertion must not print a password hash or SecurityStamp in the test log.
    private static string AccountFingerprint(User user) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(user)));

    [Theory]
    [InlineData("Admin")]
    [InlineData("HR")]
    public async Task Temporary_password_cannot_use_any_newly_shared_write_or_preview(string role)
    {
        using var factory = B43AuditTests.Factory();
        var actor = await A28TestSupport.Seed(factory, A28TestSupport.Password(), role, mustChange: true);
        using var http = A29AuthorizationTests.Client(factory, actor);
        var routes = new (string Method, string Path)[]
        {
            ("POST", "/api/jobs"), ("PUT", "/api/jobs/1"), ("POST", "/api/jobs/1/activate"), ("POST", "/api/jobs/1/deactivate"),
            ("POST", "/api/jobs/1/skills"), ("PUT", "/api/jobs/1/skills/CSHARP"), ("DELETE", "/api/jobs/1/skills/CSHARP"), ("PUT", "/api/jobs/1/skills/CSHARP/aliases"),
            ("POST", "/api/prompts"), ("POST", "/api/prompts/1/activate"), ("POST", "/api/prompts/assembled-preview"), ("POST", "/api/evaluation-sets")
        };
        foreach (var route in routes)
        {
            using var request = new HttpRequestMessage(new HttpMethod(route.Method), route.Path)
                { Content = JsonContent.Create(new { }) };
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("password_change_required", problem.RootElement.GetProperty("code").GetString());
        }
        await A28TestSupport.WithDb(factory, async db =>
        {
            Assert.Empty(await db.EvaluationSets.ToListAsync());
            Assert.Empty(await db.PromptVersions.ToListAsync());
        });
    }

    // Since #114 a System Admin is not a business operator: the Admin case is the 403 matrix of
    // B57RoleBoundaryTests.R1 (every business route, no side effects), not a success case here.
    [Theory]
    [InlineData("HR")]
    public async Task Hr_can_manage_jobs_skills_and_activation_with_trusted_audit(string role)
    {
        using var factory = B43AuditTests.Factory();
        var actor = await A28TestSupport.Seed(factory, A28TestSupport.Password(), role);
        using var http = A29AuthorizationTests.Client(factory, actor);
        var request = new JobUpsertRequest { Title = "Synthetic permissions " + Guid.NewGuid().ToString("N") };
        using var created = await http.PostAsJsonAsync("/api/jobs", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var job = (await created.Content.ReadFromJsonAsync<JobDto>())!;
        request.Title += " updated";
        using var edited = await http.PutAsJsonAsync($"/api/jobs/{job.JobId}", request);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(request.Title, (await edited.Content.ReadFromJsonAsync<JobDto>())!.Title);

        var path = $"/api/jobs/{job.JobId}/skills";
        using var skill = await http.PostAsJsonAsync(path, new JobSkillInput
            { SkillCode = "TESTING", DisplayName = "Kiểm thử", SkillType = "MustHave", Aliases = ["test"] });
        Assert.Equal(HttpStatusCode.Created, skill.StatusCode);
        using var update = await http.PutAsJsonAsync(path + "/TESTING", new UpdateJobSkillRequest { DisplayName = "Kiểm thử tự động", SkillType = "NiceToHave" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var aliases = await http.PutAsJsonAsync(path + "/TESTING/aliases", new ReplaceAliasesRequest { Aliases = ["automation"] });
        Assert.Equal(HttpStatusCode.OK, aliases.StatusCode);
        Assert.Equal(["automation"], (await aliases.Content.ReadFromJsonAsync<JobSkillDto>())!.Aliases);
        using var removed = await http.DeleteAsync(path + "/TESTING");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        foreach (var (action, expected) in new[] { ("deactivate", false), ("activate", true) })
        {
            using var response = await http.PostAsJsonAsync($"/api/jobs/{job.JobId}/{action}", new { });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(expected, (await response.Content.ReadFromJsonAsync<JobDto>())!.IsActive);
        }
        var events = (await B43AuditTests.Events(factory)).Where(e => e.ActionCode.StartsWith("jobs.")).ToList();
        Assert.Equal(8, events.Count);
        Assert.All(events, e => Assert.Equal((actor.UserId, role, "Succeeded"), (e.ActorUserId, e.ActorRoleSnapshot, e.Outcome)));
    }

    // Since #114 a System Admin is not a business operator: the Admin case is the 403 matrix of
    // B57RoleBoundaryTests.R1 (every business route, no side effects), not a success case here.
    [Theory]
    [InlineData("HR")]
    public async Task Hr_can_upload_assign_author_prompts_label_manage_sets_and_start_tests(string role)
    {
        using var factory = B43AuditTests.Factory();
        var originalAdmin = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        var actor = await A28TestSupport.Seed(factory, A28TestSupport.Password(), role);
        var adminBefore = AccountFingerprint(originalAdmin);
        using var http = A29AuthorizationTests.Client(factory, actor);
        var cv1 = await B43AuditTests.Upload(http, TestSupport.Pdf(Guid.NewGuid().ToString("N")));
        var cv2 = await B43AuditTests.Upload(http, TestSupport.Pdf(Guid.NewGuid().ToString("N")));
        using var createdJob = await http.PostAsJsonAsync("/api/jobs", new JobUpsertRequest
            { Title = "Synthetic workflow " + Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.Created, createdJob.StatusCode);
        var jobId = (await createdJob.Content.ReadFromJsonAsync<JobDto>())!.JobId;
        await A28TestSupport.WithDb(factory, async db =>
        {
            foreach (var cv in await db.Cvs.Where(c => c.CvId == cv1 || c.CvId == cv2).ToListAsync())
            {
                cv.ExtractionStatus = "Completed";
                cv.ExtractedText = "Synthetic C# SQL Server experience. " + new string('x', 600);
            }
            await db.SaveChangesAsync(); // Extraction prerequisite, never an application SQL database.
        });
        using var assigned = await http.PostAsJsonAsync("/api/applications", new CreateApplicationsRequest { JobId = jobId, CvIds = [cv1, cv2] });
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        var apps = (await assigned.Content.ReadFromJsonAsync<CreateApplicationsResponse>())!;
        Assert.Equal(2, apps.CreatedCount);
        var ids = apps.Items.Select(a => a.ApplicationId!.Value).ToArray();
        using var promptResponse = await http.PostAsJsonAsync("/api/prompts", new CreatePromptVersionRequest
            { VersionName = "Permissions " + Guid.NewGuid().ToString("N"), SystemPrompt = "Synthetic assessment rules." });
        Assert.Equal(HttpStatusCode.Created, promptResponse.StatusCode);
        var prompt = (await promptResponse.Content.ReadFromJsonAsync<PromptVersionDto>())!;
        using var preview = await http.PostAsJsonAsync("/api/prompts/assembled-preview", new { applicationId = ids[0], promptVersionId = prompt.PromptVersionId });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        // The activation transaction requires SQL Server. A missing ID proves authorization reaches
        // the real handler without claiming that InMemory verified relational activation/locking.
        using var missing = await http.PostAsJsonAsync("/api/prompts/2147483647/activate", new { });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await A28TestSupport.WithDb(factory, async db =>
        {
            (await db.PromptVersions.SingleAsync()).IsActive = true;
            await db.SaveChangesAsync(); // Active-prompt fixture prerequisite, not a relational activation test.
        });
        using var screening = await http.PostAsJsonAsync("/api/runs", new StartRunRequest { ApplicationIds = [.. ids] });
        Assert.Equal(HttpStatusCode.Created, screening.StatusCode);

        using var createdSet = await http.PostAsJsonAsync("/api/evaluation-sets", new CreateEvaluationSetRequest
            { Name = "Permissions " + Guid.NewGuid().ToString("N"), JobId = jobId, Purpose = "Synthetic permission verification" });
        Assert.Equal(HttpStatusCode.Created, createdSet.StatusCode);
        var set = (await createdSet.Content.ReadFromJsonAsync<EvaluationSetDto>())!;
        var setPath = $"/api/evaluation-sets/{set.EvaluationSetId}";
        using var add = await http.PostAsJsonAsync(setPath + "/members", new AddEvaluationSetMembersRequest
            { ApplicationIds = [.. ids], AddedBy = "Forged Admin identity" });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        Assert.Equal(2, (await add.Content.ReadFromJsonAsync<AddEvaluationSetMembersResult>())!.Added);
        using var labels = await http.PutAsJsonAsync(setPath + "/labels", new SaveHrLabelsRequest
            { LabeledBy = "Forged Admin identity", Labels = [new() { ApplicationId = ids[0], IsPass = true }, new() { ApplicationId = ids[1], IsPass = false }] });
        Assert.Equal(HttpStatusCode.OK, labels.StatusCode);
        await A28TestSupport.WithDb(factory, async db => Assert.All(
            await db.HrLabels.Where(l => l.EvaluationSetId == set.EvaluationSetId).ToListAsync(), l => Assert.Equal(actor.FullName, l.LabeledBy)));
        using var run = await http.PostAsJsonAsync("/api/prompt-tests", new StartPromptTestRequest
            { PromptVersionId = prompt.PromptVersionId, EvaluationSetId = set.EvaluationSetId });
        Assert.Equal(HttpStatusCode.Created, run.StatusCode);
        var progress = (await run.Content.ReadFromJsonAsync<PromptTestRunDto>())!;
        Assert.Equal(2, progress.TotalItems);
        using var status = await http.GetAsync($"/api/prompt-tests/{progress.RunId}");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using var clear = await http.DeleteAsync(setPath + $"/labels/{ids[0]}");
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        using var remove = await http.DeleteAsync(setPath + $"/members/{ids[1]}");
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        using var history = await http.GetAsync("/api/runs");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        await A28TestSupport.WithDb(factory, async db =>
        {
            Assert.Equal(actor.FullName, (await db.EvaluationSetMembers.SingleAsync(m => m.EvaluationSetId == set.EvaluationSetId)).AddedBy);
            Assert.Equal(adminBefore, AccountFingerprint(await db.Users.AsNoTracking().SingleAsync(u => u.UserId == originalAdmin.UserId)));
        });
        var audit = await B43AuditTests.Events(factory);
        foreach (var action in new[] { "prompts.created", "runs.started", "evaluation_sets.created", "evaluation_sets.members_added", "evaluation_sets.labels_saved", "evaluation_sets.label_removed", "evaluation_sets.member_removed" })
            Assert.Contains(audit, e => e.ActionCode == action && e.ActorUserId == actor.UserId && e.ActorRoleSnapshot == role && e.Outcome == "Succeeded");
        Assert.DoesNotContain(audit, e => e.ActorNameSnapshot == "Forged Admin identity");
    }
}
