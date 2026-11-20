using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Tests.Applications;

namespace SmartCv.Tests.Auth;

public class A29AuditTests
{
    [Fact]
    public async Task Labels_CSV_and_members_use_the_authenticated_operator_instead_of_client_fields()
    {
        using var factory = A28TestSupport.Factory();
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        var ids = await ApplicationStageEndpointTests.SeedAsync(factory.Services, 2);
        var set = new EvaluationSet { Name = "A29 synthetic set" };
        await A28TestSupport.WithDb(factory, async db =>
        {
            foreach (var cv in await db.Cvs.ToListAsync()) cv.ExtractedText = "Synthetic text for HR labels";
            db.EvaluationSets.Add(set); await db.SaveChangesAsync();
        });
        using var http = A29AuthorizationTests.Client(factory, user);
        using var member = await http.PostAsJsonAsync($"/api/evaluation-sets/{set.EvaluationSetId}/members", new { applicationIds = ids, addedBy = "FORGED" });
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
        using var labels = await http.PutAsJsonAsync($"/api/evaluation-sets/{set.EvaluationSetId}/labels", new
        { labeledBy = "FORGED", labels = new[] { new { applicationId = ids[0], isPass = true } } });
        Assert.Equal(HttpStatusCode.OK, labels.StatusCode);
        using var csv = new MultipartFormDataContent();
        csv.Add(new StringContent($"ApplicationId,IsPass,Note\n{ids[1]},false,Synthetic CSV note\n", Encoding.UTF8), "file", "synthetic.csv");
        csv.Add(new StringContent("FORGED"), "labeledBy");
        using var imported = await http.PostAsync($"/api/evaluation-sets/{set.EvaluationSetId}/labels/import", csv);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        await A28TestSupport.WithDb(factory, async db =>
        {
            Assert.Equal(2, await db.HrLabels.CountAsync());
            Assert.All(await db.HrLabels.ToListAsync(), l => Assert.Equal(user.FullName, l.LabeledBy));
            Assert.All(await db.EvaluationSetMembers.ToListAsync(), m => Assert.Equal(user.FullName, m.AddedBy));
        });
    }

    [Fact]
    public async Task Manual_flag_open_resolve_reopen_history_uses_current_identity_without_creating_stage_events()
    {
        using var factory = A28TestSupport.Factory();
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        var id = (await ApplicationStageEndpointTests.SeedAsync(factory.Services, 1))[0];
        using var http = A29AuthorizationTests.Client(factory, user);
        using var created = await http.PostAsJsonAsync($"/api/applications/{id}/flags", new { reason = "ScoreDoubt", actor = "FORGED" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var flag = (await created.Content.ReadFromJsonAsync<ManualFlagDto>())!;
        Assert.Equal(user.FullName, flag.CreatedBy);
        using var resolved = await http.PostAsJsonAsync($"/api/applications/flags/{flag.FlagId}/resolve", new { expectedVersion = flag.Version, actor = "FORGED" });
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var done = (await resolved.Content.ReadFromJsonAsync<ManualFlagDto>())!;
        Assert.Equal(user.FullName, done.ResolvedBy);
        using var reopened = await http.PostAsJsonAsync($"/api/applications/flags/{flag.FlagId}/reopen", new { expectedVersion = done.Version });
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        await A28TestSupport.WithDb(factory, async db =>
        {
            var events = await db.ApplicationFlagEvents.ToListAsync(); Assert.Equal(3, events.Count);
            Assert.All(events, e => Assert.Equal(user.FullName, e.Actor));
            Assert.Empty(await db.ApplicationEvents.ToListAsync());
        });
    }

    [Fact]
    public async Task Candidate_export_ignores_query_actor_and_records_current_operator()
    {
        using var factory = A28TestSupport.Factory();
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        var candidate = new Candidate { FullName = "Synthetic A29 candidate", CreatedAt = DateTime.UtcNow };
        await A28TestSupport.WithDb(factory, async db => { db.Candidates.Add(candidate); await db.SaveChangesAsync(); });
        using var http = A29AuthorizationTests.Client(factory, user);
        using var response = await http.GetAsync($"/api/candidates/{candidate.CandidateId}/data-export?handledBy=FORGED&userId=999&role=Admin");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(user.FullName, (await A29AuthorizationTests.Json(response)).GetProperty("exportedBy").GetString());
        await A28TestSupport.WithDb(factory, async db => Assert.Equal(user.FullName, (await db.CandidateDataRequests.SingleAsync()).HandledBy));
    }

    [Fact]
    public async Task Kestrel_HTTP_login_then_authorized_decision_and_history_use_the_same_account()
    {
        using var factory = A28TestSupport.Factory();
        factory.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var http = factory.CreateClient();
        var password = A28TestSupport.Password(); var user = await A28TestSupport.Seed(factory, password, "HR");
        var id = (await ApplicationStageEndpointTests.SeedAsync(factory.Services, 1))[0];
        using var login = await http.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        http.DefaultRequestHeaders.Authorization = new("Bearer", session.Token);
        using var job = await http.PostAsJsonAsync("/api/jobs", new { title = "Synthetic HR recruitment job" });
        Assert.Equal(HttpStatusCode.Created, job.StatusCode);
        // The approved matrix opens recruitment writes; account administration remains forbidden.
        using var forbidden = await http.PostAsJsonAsync("/api/users", new { fullName = "FORGED", role = "Admin" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var changed = await http.PatchAsJsonAsync($"/api/applications/{id}/stage", new { toStage = "Reviewing", actor = "FORGED", userId = 999 });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var history = (await http.GetFromJsonAsync<List<ApplicationEventDto>>($"/api/applications/{id}/events"))!;
        Assert.Equal(user.UserId, Assert.Single(history).UserId);
        Assert.Equal(user.FullName, history[0].Actor);
    }
}
