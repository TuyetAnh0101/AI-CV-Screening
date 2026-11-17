using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Jobs;

public class JobSkillEndpointTests
{
    internal static JobSkillInput Skill(string? code = "ASPNET_CORE", string name = "ASP.NET Core", string type = "MustHave") =>
        new() { SkillCode = code, DisplayName = name, SkillType = type, Aliases = ["  DOT\tNET ", "dot net", null, ""] };

    [Fact]
    public async Task Four_endpoints_roundtrip_normalized_skills_aliases_and_keep_other_jobs_intact()
    {
        using var factory = JobCrudEndpointTests.Factory(); await JobCrudEndpointTests.SeedAsync(factory); using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/jobs/1/skills", Skill(null, "Kiểm thử tự động"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); Assert.True(created.Headers.CacheControl!.NoStore);
        Assert.Equal("/api/jobs/1/skills/KIEM_THU_TU_DONG", created.Headers.Location!.OriginalString);
        var dto = (await created.Content.ReadFromJsonAsync<JobSkillDto>())!;
        Assert.Equal("KIEM_THU_TU_DONG", dto.SkillCode); Assert.Equal(["dot net"], dto.Aliases);
        using var duplicate = await client.PostAsJsonAsync("/api/jobs/1/skills", Skill("kiem_thu_tu_dong"));
        await JobCrudEndpointTests.AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "skillCode");
        using var other = await client.PostAsJsonAsync("/api/jobs/2/skills", Skill("KIEM_THU_TU_DONG")); Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        using var edit = await client.PutAsJsonAsync(created.Headers.Location, new UpdateJobSkillRequest { DisplayName = "  Tự động hóa  ", SkillType = "NiceToHave" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode); var edited = (await edit.Content.ReadFromJsonAsync<JobSkillDto>())!;
        Assert.Equal("Tự động hóa", edited.DisplayName); Assert.Equal("NiceToHave", edited.SkillType); Assert.Equal(dto.SkillCode, edited.SkillCode);
        var path = created.Headers.Location!.OriginalString.ToLowerInvariant();
        using var aliases = await client.PutAsJsonAsync(path + "/aliases", new ReplaceAliasesRequest { Aliases = [" SELENIUM ", "Web\nDriver", "selenium"] });
        Assert.Equal(HttpStatusCode.OK, aliases.StatusCode); Assert.Equal(["selenium", "web driver"], (await aliases.Content.ReadFromJsonAsync<JobSkillDto>())!.Aliases);
        using var repeat = await client.PutAsJsonAsync(path + "/aliases", new ReplaceAliasesRequest { Aliases = ["selenium", "web driver"] }); Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        using var clear = await client.PutAsJsonAsync(path + "/aliases", new ReplaceAliasesRequest { Aliases = [] });
        Assert.Empty((await clear.Content.ReadFromJsonAsync<JobSkillDto>())!.Aliases);
        using var delete = await client.DeleteAsync(path); Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode); Assert.Empty(await delete.Content.ReadAsByteArrayAsync());
        using var twice = await client.DeleteAsync(path); await JobCrudEndpointTests.AssertProblemAsync(twice, HttpStatusCode.NotFound);
        Assert.DoesNotContain((await client.GetFromJsonAsync<JobDto>("/api/jobs/1"))!.Skills, s => s.SkillCode == dto.SkillCode);
        Assert.Contains((await client.GetFromJsonAsync<JobDto>("/api/jobs/2"))!.Skills, s => s.SkillCode == dto.SkillCode);
    }

    [Fact]
    public async Task Legacy_lowercase_keys_are_not_renamed_and_used_skill_cannot_be_deleted()
    {
        using var factory = JobCrudEndpointTests.Factory(); await JobCrudEndpointTests.SeedAsync(factory); using var client = factory.CreateClient();
        using var duplicate = await client.PostAsJsonAsync("/api/jobs/1/skills", Skill(null, "C#"));
        await JobCrudEndpointTests.AssertProblemAsync(duplicate, HttpStatusCode.Conflict);
        using var edit = await client.PutAsJsonAsync("/api/jobs/1/skills/CSHARP", new UpdateJobSkillRequest { DisplayName = "C# backend", SkillType = "NiceToHave" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode); Assert.Equal("csharp", (await edit.Content.ReadFromJsonAsync<JobSkillDto>())!.SkillCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            // InMemory does not enforce FKs; SQL tests below persist the full evaluation graph.
            db.EvaluationSkills.Add(new EvaluationSkill { EvaluationId = 123, JobId = 1, SkillCode = "csharp", IsMatched = true, Evidence = "unchanged" }); await db.SaveChangesAsync();
        }
        using var used = await client.DeleteAsync("/api/jobs/1/skills/CSHARP"); await JobCrudEndpointTests.AssertProblemAsync(used, HttpStatusCode.Conflict);
        using var check = factory.Services.CreateScope(); var context = check.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        Assert.Equal("unchanged", (await context.EvaluationSkills.SingleAsync()).Evidence); Assert.True(await context.JobSkillAliases.AnyAsync(a => a.JobId == 1 && a.SkillCode == "csharp"));
    }

    [Theory]
    [InlineData("POST", "/api/jobs/1/skills", "null", "body")]
    [InlineData("POST", "/api/jobs/1/skills", "{", "body")]
    [InlineData("POST", "/api/jobs/1/skills", "{}", "displayName")]
    [InlineData("POST", "/api/jobs/1/skills", "{\"displayName\":\"C#\",\"skillType\":\"Required\"}", "skillType")]
    [InlineData("POST", "/api/jobs/1/skills", "{\"displayName\":\"C#\",\"skillType\":\"MustHave\",\"aliases\":[1]}", "aliases")]
    [InlineData("POST", "/api/jobs/1/skills", "{\"displayName\":\"\\ud800\",\"skillType\":\"MustHave\"}", "displayName")]
    [InlineData("PUT", "/api/jobs/1/skills/csharp", "{\"skillCode\":\"NEW\",\"displayName\":\"C#\",\"skillType\":\"MustHave\"}", "skillCode")]
    [InlineData("PUT", "/api/jobs/1/skills/csharp/aliases", "{}", "aliases")]
    [InlineData("PUT", "/api/jobs/1/skills/csharp/aliases", "{\"aliases\":null}", "aliases")]
    [InlineData("PUT", "/api/jobs/1/skills/csharp/aliases", "{\"aliases\":\"oops\"}", "aliases")]
    public async Task Invalid_bodies_have_field_errors(string method, string path, string json, string field)
    {
        using var factory = JobCrudEndpointTests.Factory(); await JobCrudEndpointTests.SeedAsync(factory); using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request); await JobCrudEndpointTests.AssertProblemAsync(response, HttpStatusCode.BadRequest, field);
    }

    [Fact]
    public async Task Malformed_nested_Job_skills_report_the_exact_field_without_creating_a_Job()
    {
        using var factory = JobCrudEndpointTests.Factory(); await JobCrudEndpointTests.SeedAsync(factory); using var client = factory.CreateClient();
        using var body = new StringContent("{\"title\":\"A22 malformed nested\",\"skills\":[{\"displayName\":\"C#\",\"skillType\":\"MustHave\",\"aliases\":[42]}]}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/jobs", body); await JobCrudEndpointTests.AssertProblemAsync(response, HttpStatusCode.BadRequest, "skills[0].aliases[0]");
        using var scope = factory.Services.CreateScope(); Assert.Equal(5, await scope.ServiceProvider.GetRequiredService<SmartCvDbContext>().Jobs.CountAsync());
    }

    [Fact]
    public async Task Missing_jobs_skills_bad_routes_alias_limits_and_transport_errors_are_explicit()
    {
        using var factory = JobCrudEndpointTests.Factory(); await JobCrudEndpointTests.SeedAsync(factory); using var client = factory.CreateClient();
        using var job = await client.PostAsJsonAsync("/api/jobs/999/skills", Skill()); await JobCrudEndpointTests.AssertProblemAsync(job, HttpStatusCode.NotFound);
        foreach (var path in new[] { "/api/jobs/999/skills/MISSING", "/api/jobs/1/skills/MISSING" })
        {
            using var missing = await client.PutAsJsonAsync(path, new UpdateJobSkillRequest()); await JobCrudEndpointTests.AssertProblemAsync(missing, HttpStatusCode.NotFound);
            using var aliases = await client.PutAsJsonAsync(path + "/aliases", new ReplaceAliasesRequest { Aliases = [] }); await JobCrudEndpointTests.AssertProblemAsync(aliases, HttpStatusCode.NotFound);
            using var delete = await client.DeleteAsync(path); await JobCrudEndpointTests.AssertProblemAsync(delete, HttpStatusCode.NotFound);
        }
        using var id = await client.PostAsJsonAsync("/api/jobs/0/skills", Skill()); await JobCrudEndpointTests.AssertProblemAsync(id, HttpStatusCode.BadRequest, "id");
        using var code = await client.DeleteAsync("/api/jobs/1/skills/_BAD"); await JobCrudEndpointTests.AssertProblemAsync(code, HttpStatusCode.BadRequest, "skillCode");
        foreach (var aliases in new[] { Enumerable.Range(0, 31).Select(i => (string?)$"alias{i}").ToList(), [new string('x', 101)] })
        {
            using var limit = await client.PutAsJsonAsync("/api/jobs/1/skills/csharp/aliases", new ReplaceAliasesRequest { Aliases = aliases }); await JobCrudEndpointTests.AssertProblemAsync(limit, HttpStatusCode.BadRequest, "aliases");
        }
        using var text = await client.PostAsync("/api/jobs/1/skills", new StringContent("{}")); Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
        using var empty = await client.PostAsync("/api/jobs/1/skills", null); await JobCrudEndpointTests.AssertProblemAsync(empty, HttpStatusCode.BadRequest, "body");
        using var huge = await client.PostAsync("/api/jobs/1/skills", new StringContent(new string('x', 256 * 1024 + 1), Encoding.UTF8, "application/json")); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", true)]
    [InlineData("Staging", false)]
    [InlineData("Development", false)]
    public async Task Writes_are_absent_outside_internal_gate_even_with_forged_HR_header(string environment, bool enabled)
    {
        using var factory = JobCrudEndpointTests.Factory(environment, enabled);
        if (environment != "Development" && enabled)
        {
            // The existing upload startup guard rejects this configuration before any route is served.
            var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
            Assert.Contains("M6", exception.Message); return;
        }
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-HR", "admin");
        using var add = await client.PostAsJsonAsync("/api/jobs/1/skills", Skill()); Assert.Equal(HttpStatusCode.NotFound, add.StatusCode);
        using var edit = await client.PutAsJsonAsync("/api/jobs/1/skills/csharp", new UpdateJobSkillRequest()); Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        using var aliases = await client.PutAsJsonAsync("/api/jobs/1/skills/csharp/aliases", new ReplaceAliasesRequest()); Assert.Equal(HttpStatusCode.NotFound, aliases.StatusCode);
        using var delete = await client.DeleteAsync("/api/jobs/1/skills/csharp"); Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task Job_create_validates_entire_graph_and_Job_update_ignores_skills()
    {
        using var factory = JobCrudEndpointTests.Factory(); await JobCrudEndpointTests.SeedAsync(factory); using var client = factory.CreateClient();
        var request = JobRulesTests.Valid("A22 graph"); request.Skills = [Skill(), Skill("SQL", "SQL", "MustHave"), Skill("GIT", "Git", "NiceToHave")];
        using var response = await client.PostAsJsonAsync("/api/jobs", request); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<JobDto>())!; Assert.Equal(3, job.Skills.Count); Assert.Equal(2, job.MustHaveCount);
        request.Skills = [new JobSkillInput()];
        using var edit = await client.PutAsJsonAsync($"/api/jobs/{job.JobId}", request); Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(JsonSerializer.Serialize(job.Skills), JsonSerializer.Serialize((await edit.Content.ReadFromJsonAsync<JobDto>())!.Skills));
        request.Title = "Invalid A22 graph";
        using var invalid = await client.PostAsJsonAsync("/api/jobs", request); await JobCrudEndpointTests.AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "skills[0].displayName");
        request.Skills = [Skill("sql"), Skill(" SQL ")];
        using var duplicate = await client.PostAsJsonAsync("/api/jobs", request); await JobCrudEndpointTests.AssertProblemAsync(duplicate, HttpStatusCode.BadRequest, "skills");
        request.Skills = [null!];
        using var nullSkill = await client.PostAsJsonAsync("/api/jobs", request); await JobCrudEndpointTests.AssertProblemAsync(nullSkill, HttpStatusCode.BadRequest, "skills[0].body");
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        Assert.False(await db.Jobs.AnyAsync(j => j.Title == request.Title)); Assert.Equal(6, await db.Jobs.CountAsync());
    }
}
