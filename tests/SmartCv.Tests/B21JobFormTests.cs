extern alias web;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SmartCv.Core.Common;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Models;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

public class B21JobFormTests
{
    private static SkillFormModel Skill(string code = "SQL", string name = "SQL", string type = "MustHave") => new() { SkillCode = code, DisplayName = name, SkillType = type, Aliases = ["sql"] };
    private static JobFormModel Valid() => new() { Title = "Backend", Skills = [Skill()], Headcount = 2, ClosingDate = VietnamTime.Today().AddDays(30) };
    private static List<ValidationResult> Validate(JobFormModel model) { var results = new List<ValidationResult>(); Validator.TryValidateObject(model, new(model), results, true); return results; }
    [Theory]
    [InlineData("Title", "", false)] [InlineData("Title", "   ", false)] [InlineData("Title", "Backend", true)]
    [InlineData("MinGpa", "-0.1", false)] [InlineData("MinGpa", "0", true)] [InlineData("MinGpa", "4", true)] [InlineData("MinGpa", "4.1", false)]
    [InlineData("MinYearsExperience", "-1", false)] [InlineData("MinYearsExperience", "0", true)] [InlineData("MinYearsExperience", "50", true)] [InlineData("MinYearsExperience", "50.1", false)]
    [InlineData("Headcount", "0", false)] [InlineData("Headcount", "1", true)] [InlineData("Headcount", "1000", true)] [InlineData("Headcount", "1001", false)]
    public void Field_validation_matches_A21(string property, string value, bool valid)
    {
        var model = Valid(); var p = typeof(JobFormModel).GetProperty(property)!; var type = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        p.SetValue(model, Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(valid, !Validate(model).Any(v => v.MemberNames.Contains(property)));
    }
    [Fact] public void Length_date_null_and_MustHave_rules_are_enforced_without_rejecting_normalized_titles()
    {
        var model = Valid(); model.Title = new('a', 151); Assert.Contains(Validate(model), v => v.MemberNames.Contains("Title"));
        model.Title = "  " + new string('a', 150) + "  "; Assert.Empty(Validate(model));
        model.Description = new('x', 20001); Assert.Contains(Validate(model), v => v.MemberNames.Contains("Description")); model.Description = null;
        model.ClosingDate = VietnamTime.Today().AddDays(-1); Assert.Contains(Validate(model), v => v.MemberNames.Contains("ClosingDate")); model.IsNew = false; Assert.Empty(Validate(model));
        model.MinGpa = null; model.Headcount = null; model.ClosingDate = null; Assert.Empty(Validate(model));
        model.Skills[0].SkillType = "NiceToHave"; Assert.Contains(Validate(model), v => v.MemberNames.Contains("Skills"));
    }
    [Theory]
    [InlineData("ASP.NET Core", "ASPNET_CORE")] [InlineData("C#", "CSHARP")] [InlineData("C++", "CPLUSPLUS")] [InlineData("Kiểm thử tự động", "KIEM_THU_TU_DONG")]
    public void Suggested_code_uses_A22_rules(string name, string expected) => Assert.Equal(expected, new SkillFormModel { DisplayName = name }.EffectiveCode);
    [Fact] public void Normalized_duplicates_immutable_saved_codes_and_alias_limits_are_validated_per_row()
    {
        var model = Valid(); model.Skills.Add(Skill("sql", "Database")); Assert.NotEmpty(Validate(model)); Assert.Contains("trùng", model.Skills[0].Validation["skillCode"]);
        model.Skills.RemoveAt(1); var row = model.Skills[0]; row.IsSaved = true; row.SavedCode = "OLD"; Assert.NotEmpty(Validate(model));
        row.SavedCode = row.SkillCode; row.Aliases = Enumerable.Range(0, 31).Select(i => "tag" + i).ToList(); Assert.NotEmpty(Validate(model)); Assert.Contains("30", row.Validation["aliases"]);
        row.Aliases = [new string('x', 101)]; Assert.NotEmpty(Validate(model));
        row.Aliases = ["  SQL  server  ", "sql server"]; row.AliasDraft = " .NET , dotnet,SQL server"; Assert.Empty(Validate(model)); Assert.Equal(new[] { "sql server", ".net", "dotnet" }, row.NormalizeAliases().Aliases);
    }

    private static JobDto Initial(bool? stats = true) => new()
    {
        JobId = 1, Title = "Backend", Description = "Initial", Headcount = 2, IsActive = true, HasEvaluations = stats,
        Skills = [new() { SkillCode = "CSHARP", DisplayName = "C#", SkillType = "MustHave", Aliases = ["csharp"] }, new() { SkillCode = "SQL", DisplayName = "SQL", SkillType = "MustHave", Aliases = ["sql"] }, new() { SkillCode = "OLD", DisplayName = "Old", SkillType = "NiceToHave" }]
    };
    private sealed class Server : HttpMessageHandler
    {
        public JobDto Job = Initial();
        public List<(string Method, string Path, string Body)> Calls { get; } = [];
        public Func<HttpRequestMessage, int?> Failure = _ => null;
        public bool LoseNextWrite, InvalidJson, InvalidShape, ReadOffline;
        public TaskCompletionSource? Gate;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ReadToken;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath; var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, request.RequestUri.PathAndQuery, body));
            if (request.Method == HttpMethod.Get && ReadOffline) throw new HttpRequestException("PRIVATE network details");
            if (Gate is not null) { ReadToken = ct; Started.TrySetResult(); await Gate.Task.WaitAsync(ct); }
            if (InvalidJson) return new(HttpStatusCode.OK) { Content = new StringContent("{bad json", System.Text.Encoding.UTF8, "application/json") };
            if (InvalidShape) return Json(new { jobId = 9, title = "Invalid shape", skills = (object?)null });
            if (Failure(request) is { } status) return Json(new { title = "Fixture error", detail = "PRIVATE failure", errors = request.Method == HttpMethod.Delete && status == 409 ? new Dictionary<string, string[]>() : new Dictionary<string, string[]> { [path.EndsWith("/aliases") ? "aliases" : "title"] = ["Dữ liệu chưa hợp lệ."] } }, status);
            if (request.Method == HttpMethod.Get) return path == "/api/jobs" ? Json(new[] { Job }) : Json(Job);
            object? result = null; var code = path.Split('/').Last();
            if (path is "/api/jobs" or "/api/jobs/1")
            {
                var input = JsonSerializer.Deserialize<JobUpsertRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Job = new() { JobId = path == "/api/jobs" ? 9 : 1, Title = input.Title!, Description = input.Description, Headcount = input.Headcount, ClosingDate = input.ClosingDate, MinGpa = input.MinGpa, MinYearsExperience = input.MinYearsExperience, IsActive = true, HasEvaluations = false, Skills = input.Skills is null ? Job.Skills : input.Skills.Select(s => new JobSkillDto { SkillCode = s.SkillCode!, DisplayName = s.DisplayName!, SkillType = s.SkillType!, Aliases = SmartCv.Core.Jobs.JobSkillRules.NormalizeAliases(s.Aliases).Aliases }).ToList() }; result = Job;
            }
            else if (request.Method == HttpMethod.Delete) Job.Skills.RemoveAll(s => s.SkillCode == code);
            else if (request.Method == HttpMethod.Post)
            {
                var s = JsonSerializer.Deserialize<JobSkillInput>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var skill = new JobSkillDto { SkillCode = s.SkillCode!, DisplayName = s.DisplayName!, SkillType = s.SkillType!, Aliases = SmartCv.Core.Jobs.JobSkillRules.NormalizeAliases(s.Aliases).Aliases }; Job.Skills.Add(skill); result = skill;
            }
            else
            {
                var target = code == "aliases" ? path.Split('/')[5] : code; var old = Job.Skills.Single(s => s.SkillCode == target);
                using var json = JsonDocument.Parse(body);
                var updated = code == "aliases" ? new JobSkillDto { SkillCode = target, DisplayName = old.DisplayName, SkillType = old.SkillType, Aliases = JsonSerializer.Deserialize<ReplaceAliasesRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Aliases!.Select(s => s!).ToList() }
                    : new JobSkillDto { SkillCode = target, DisplayName = json.RootElement.GetProperty("displayName").GetString()!, SkillType = json.RootElement.GetProperty("skillType").GetString()!, Aliases = old.Aliases };
                Job.Skills[Job.Skills.IndexOf(old)] = updated; result = updated;
            }
            if (LoseNextWrite) { LoseNextWrite = false; throw new TaskCanceledException("Applied but response lost"); }
            return result is null ? new(HttpStatusCode.NoContent) : Json(result);
        }
        private static HttpResponseMessage Json(object value, int status = 200) => new((HttpStatusCode)status) { Content = JsonContent.Create(value, value.GetType()) };
    }
    private static JobApiClient Api(Server server) => new(new HttpClient(server) { BaseAddress = new("https://api.test/") }, NullLogger<JobApiClient>.Instance);
    private static List<(string Method, string Path, string Body)> Writes(Server s) => s.Calls.Where(c => c.Method != "GET").ToList();

    [Fact] public async Task Create_sends_one_POST_with_skills_aliases_DateOnly_and_never_an_extra_skill_POST()
    {
        var server = new Server(); using var form = new JobFormWorkspace(Api(server), null); await form.LoadAsync();
        form.Model.Title = "New job"; form.Model.ClosingDate = VietnamTime.Today(); form.Model.Skills.Add(Skill()); form.Model.Skills[0].AliasDraft = " SQL, Sql server";
        var id = await form.SaveAsync(); Assert.Equal(9, id); var write = Assert.Single(Writes(server)); Assert.Equal("POST", write.Method); Assert.Equal("/api/jobs", write.Path);
        using var json = JsonDocument.Parse(write.Body); Assert.Equal(VietnamTime.Today().ToString("yyyy-MM-dd"), json.RootElement.GetProperty("closingDate").GetString()); Assert.Single(json.RootElement.GetProperty("skills").EnumerateArray());
        Assert.False(form.IsDirty); Assert.Contains("Đã tạo", form.Notice);
    }
    [Fact] public async Task Create_409_maps_title_field_and_keeps_draft()
    {
        var server = new Server { Failure = r => r.Method == HttpMethod.Post ? 409 : null }; using var form = new JobFormWorkspace(Api(server), null); await form.LoadAsync(); form.Model.Title = "Duplicate"; form.Model.Skills.Add(Skill());
        await form.SaveAsync(); Assert.Contains("title", form.FieldErrors.Keys); Assert.Equal("Duplicate", form.Model.Title); Assert.True(form.IsDirty); Assert.False(form.NeedsVerification);
    }
    [Fact] public async Task Timeout_create_does_not_replay_POST_and_manual_GET_finds_actual_job()
    {
        var server = new Server { LoseNextWrite = true }; using var form = new JobFormWorkspace(Api(server), null); await form.LoadAsync(); form.Model.Title = "New job"; form.Model.Skills.Add(Skill());
        await form.SaveAsync(); Assert.True(form.NeedsVerification); await form.SaveAsync(); Assert.Single(Writes(server));
        Assert.Equal(9, await form.VerifyAsync()); Assert.False(form.NeedsVerification); Assert.Single(Writes(server));
    }
    [Fact] public async Task Existing_no_changes_makes_no_write_or_extra_GET_and_preserves_legacy_key()
    {
        var server = new Server(); using var form = new JobFormWorkspace(Api(server), 1); await form.LoadAsync(); await form.SaveAsync(); Assert.Single(server.Calls); Assert.Empty(Writes(server));
        form.Model.Skills[0].Aliases = [" CSHARP ", "csharp"]; await form.SaveAsync(); Assert.Empty(Writes(server));
        Assert.Equal("CSHARP", form.Model.Skills[0].SavedCode);
    }
    [Fact] public async Task SkillDiff_orders_delete_add_update_aliases_and_only_sends_needed_fields()
    {
        var server = new Server(); using var form = new JobFormWorkspace(Api(server), 1); await form.LoadAsync();
        form.Model.Skills[2].Deleted = true; form.Model.Skills.Add(Skill("NEW", "New")); var sql = form.Model.Skills[1]; sql.DisplayName = "SQL Server"; sql.Aliases = ["sql server"];
        await form.SaveAsync(); Assert.Equal(new[] { "DELETE", "POST", "PUT", "PUT" }, Writes(server).Select(c => c.Method)); Assert.EndsWith("/aliases", Writes(server)[3].Path);
        Assert.DoesNotContain("skillCode", Writes(server)[2].Body); Assert.Equal(0, form.PendingCount);
        var before = server.Calls.Count; await form.SaveAsync(); Assert.Equal(before, server.Calls.Count);
    }
    [Fact] public async Task Delete_409_restores_row_preserves_other_saved_changes_and_is_not_automatically_reissued()
    {
        var server = new Server { Failure = r => r.Method == HttpMethod.Delete ? 409 : null }; using var form = new JobFormWorkspace(Api(server), 1); await form.LoadAsync();
        var csharp = form.Model.Skills[0]; csharp.Deleted = true; form.Model.Skills[1].Aliases = ["database", "sql server"];
        await form.SaveAsync(); Assert.False(csharp.Deleted); Assert.True(csharp.DeleteBlocked); Assert.Contains("lịch sử", csharp.Error); Assert.True(form.NoticeIsError); Assert.Equal(1, form.PendingCount);
        Assert.Equal(new[] { "database", "sql server" }, server.Job.Skills.Single(s => s.SkillCode == "SQL").Aliases);
        var before = Writes(server).Count; await form.SaveAsync(); Assert.Equal(before, Writes(server).Count); Assert.Equal(1, form.PendingCount);
    }
    [Fact] public async Task Partial_update_error_keeps_unsaved_metadata_and_applied_aliases_in_same_row()
    {
        var server = new Server { Failure = r => r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath == "/api/jobs/1/skills/SQL" ? 400 : null }; using var form = new JobFormWorkspace(Api(server), 1); await form.LoadAsync();
        var row = form.Model.Skills[1]; row.DisplayName = "SQL Server"; row.AliasDraft = "database";
        await form.SaveAsync(); Assert.Equal("SQL Server", row.DisplayName); Assert.Contains("database", row.Aliases); Assert.Equal("SQL", server.Job.Skills[1].DisplayName); Assert.Equal(1, form.PendingCount);
        server.Failure = _ => null; var before = Writes(server).Count; await form.SaveAsync(); Assert.Equal(before + 1, Writes(server).Count); Assert.Equal("SQL Server", server.Job.Skills[1].DisplayName); Assert.Equal(0, form.PendingCount);
    }
    [Fact] public async Task Failed_alias_replacement_keeps_pending_alias_draft_after_metadata_was_saved()
    {
        var server = new Server { Failure = r => r.RequestUri!.AbsolutePath.EndsWith("/aliases") ? 400 : null }; using var form = new JobFormWorkspace(Api(server), 1); await form.LoadAsync(); var row = form.Model.Skills[1];
        row.DisplayName = "SQL Server"; row.AliasDraft = "database"; await form.SaveAsync(); Assert.Equal("SQL Server", server.Job.Skills[1].DisplayName); Assert.Equal("database", row.AliasDraft); Assert.Equal(1, form.PendingCount);
        server.Failure = _ => null; var before = Writes(server).Count; await form.SaveAsync(); Assert.Equal(before + 1, Writes(server).Count); Assert.EndsWith("/aliases", Writes(server).Last().Path);
    }
    [Theory] [InlineData(400)] [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(409)] [InlineData(429)] [InlineData(500)] [InlineData(503)]
    public async Task Write_failures_keep_draft_and_handle_uncertain_outcomes(int status)
    {
        var server = new Server { Failure = r => r.Method == HttpMethod.Put ? status : null }; using var form = new JobFormWorkspace(Api(server), 1); await form.LoadAsync(); form.Model.Title = "Changed";
        await form.SaveAsync(); Assert.Equal("Changed", form.Model.Title); Assert.True(form.IsDirty); Assert.True(form.NoticeIsError); Assert.Equal(status >= 500, form.NeedsVerification);
        if (status >= 500) { Assert.DoesNotContain("PRIVATE", form.Notice); var before = Writes(server).Count; await form.SaveAsync(); Assert.Equal(before, Writes(server).Count); }
    }
    [Fact] public async Task Malformed_success_response_locks_create_and_never_replays_until_read_verification()
    {
        var server = new Server { InvalidJson = true }; using var form = new JobFormWorkspace(Api(server), null); await form.LoadAsync(); form.Model.Title = "New"; form.Model.Skills.Add(Skill());
        await form.SaveAsync(); Assert.True(form.NeedsVerification); await form.SaveAsync(); Assert.Single(Writes(server)); Assert.Equal("New", form.Model.Title);
        server.InvalidJson = false; await form.VerifyAsync(); Assert.False(form.NeedsVerification); Assert.Single(Writes(server)); Assert.Contains("Chưa thấy", form.Notice);
    }
    [Fact] public async Task Conflicting_create_verification_keeps_lock_and_draft_instead_of_adopting_someone_elses_criteria()
    {
        var server = new Server { Failure = r => r.Method == HttpMethod.Post ? 503 : null }; using var form = new JobFormWorkspace(Api(server), null); await form.LoadAsync(); form.Model.Title = "Backend"; form.Model.Skills.Add(Skill());
        await form.SaveAsync(); Assert.True(form.NeedsVerification); await form.VerifyAsync(); Assert.True(form.NeedsVerification); Assert.Null(form.Id); Assert.Single(form.Model.Skills); Assert.Single(Writes(server));
    }
    [Theory] [InlineData(null)] [InlineData(1)]
    public async Task Invalid_DTO_shape_is_rejected_without_render_exception_or_write_replay(int? id)
    {
        var server = new Server { InvalidShape = true }; using var form = new JobFormWorkspace(Api(server), id); await form.LoadAsync();
        if (id is not null) { Assert.NotNull(form.LoadError); return; }
        form.Model.Title = "New"; form.Model.Skills.Add(Skill()); await form.SaveAsync(); Assert.True(form.NeedsVerification); await form.SaveAsync(); Assert.Single(Writes(server)); Assert.Equal("New", form.Model.Title);
    }
    [Fact] public async Task General_409_does_not_prevent_valid_skill_changes_or_discard_unsaved_title()
    {
        var server = new Server { Failure = r => r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath == "/api/jobs/1" ? 409 : null }; using var form = new JobFormWorkspace(Api(server), 1); await form.LoadAsync();
        form.Model.Title = "Duplicate"; form.Model.Skills[1].AliasDraft = "database"; await form.SaveAsync(); Assert.Equal("Duplicate", form.Model.Title); Assert.Contains("title", form.FieldErrors.Keys); Assert.Contains("database", server.Job.Skills[1].Aliases); Assert.Equal(1, form.PendingCount);
    }
    [Fact] public async Task Real_API_duplicate_title_409_is_exposed_as_a_field_error()
    {
        using var factory = global::SmartCv.Tests.Jobs.JobCrudEndpointTests.Factory(); await global::SmartCv.Tests.Jobs.JobCrudEndpointTests.SeedAsync(factory); using var http = factory.CreateClient(); var api = new JobApiClient(http, NullLogger<JobApiClient>.Instance);
        var existing = await api.GetJobAsync(1); using var form = new JobFormWorkspace(api, null); await form.LoadAsync(); form.Model.Title = existing.Data!.Title; form.Model.Skills.Add(Skill());
        await form.SaveAsync(); Assert.Contains("title", form.FieldErrors.Keys); Assert.True(form.IsNew); Assert.False(form.NeedsVerification);
    }
    private sealed class Nav : NavigationManager
    {
        public Nav() { Initialize("https://app.test/", "https://app.test/jobs/new"); }
        protected override void NavigateToCore(string uri, bool forceLoad) { Uri = ToAbsoluteUri(uri).AbsoluteUri; NotifyLocationChanged(false); }
    }
    private sealed class Js : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
    [Theory] [InlineData(null, false)] [InlineData(1, false)] [InlineData(1, true)]
    public async Task Actual_Razor_form_renders_routes_fields_and_boolean_permissions(int? id, bool readOnly)
    {
        var server = new Server { Job = Initial(readOnly ? null : true) };
        using var sp = new ServiceCollection().AddLogging().AddSingleton<NavigationManager>(new Nav()).AddSingleton<IJSRuntime>(new Js()).AddSingleton(Api(server)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(sp, NullLoggerFactory.Instance);
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<web::SmartCv.Web.Components.Pages.JobEdit>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Id"] = id }))).ToHtmlString());
        Assert.Contains("<form", html); Assert.Contains("novalidate", html); Assert.DoesNotContain("disabled=\"Form.Locked\"", html);
        var title = System.Text.RegularExpressions.Regex.Match(html, "<input[^>]*id=\"job-title\"[^>]*>").Value;
        Assert.NotEmpty(title); Assert.Equal(readOnly, title.Contains("disabled"));
        if (id is null) Assert.Contains("Tạo vị trí", WebUtility.HtmlDecode(html));
        else Assert.Contains(readOnly ? "chưa xác định" : "đã có lượt chấm", WebUtility.HtmlDecode(html));
    }
    [Fact] public async Task Uncertain_skill_write_stops_remaining_writes_and_requires_successful_GET_verification()
    {
        var server = new Server(); using var form = new JobFormWorkspace(Api(server), 1); await form.LoadAsync(); server.LoseNextWrite = true; form.Model.Skills[1].DisplayName = "SQL Server"; form.Model.Skills[1].Aliases = ["database"];
        await form.SaveAsync(); Assert.True(form.NeedsVerification); Assert.Single(Writes(server)); server.ReadOffline = true; await form.VerifyAsync(); Assert.True(form.NeedsVerification);
        server.ReadOffline = false; await form.VerifyAsync(); Assert.False(form.NeedsVerification); Assert.Equal("SQL Server", form.Model.Skills[1].DisplayName); Assert.Equal(1, form.PendingCount);
        await form.SaveAsync(); Assert.Equal(2, Writes(server).Count); Assert.EndsWith("/aliases", Writes(server).Last().Path);
    }
    [Fact] public async Task Concurrent_save_is_ignored_while_one_POST_is_inflight()
    {
        var server = new Server { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; using var form = new JobFormWorkspace(Api(server), null); await form.LoadAsync(); form.Model.Title = "New"; form.Model.Skills.Add(Skill());
        var first = form.SaveAsync(); await server.Started.Task; await form.SaveAsync(); Assert.Single(Writes(server)); server.Gate.SetResult(); await first;
    }
    [Fact] public async Task Load_cancellation_and_malformed_JSON_do_not_discard_draft_or_throw_to_UI()
    {
        var server = new Server { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var form = new JobFormWorkspace(Api(server), 1); var load = form.LoadAsync(); await server.Started.Task; form.Dispose(); await load; Assert.True(server.ReadToken.IsCancellationRequested);
        var malformed = new Server { InvalidJson = true }; using var bad = new JobFormWorkspace(Api(malformed), 1); await bad.LoadAsync(); Assert.NotNull(bad.LoadError); Assert.DoesNotContain("PRIVATE", bad.LoadError);
    }
    [Fact] public async Task Null_HR_statistics_remain_unknown_and_prevent_writes()
    {
        var server = new Server { Job = Initial(null) }; using var form = new JobFormWorkspace(Api(server), 1); await form.LoadAsync(); Assert.Null(form.Original!.HasEvaluations); Assert.False(form.CanWrite); form.Model.Title = "Changed"; await form.SaveAsync(); Assert.Empty(Writes(server));
    }
    [Fact] public async Task Actual_A21_A22_single_create_edit_aliases_delete204_and_used409_preserve_history()
    {
        using var factory = global::SmartCv.Tests.Jobs.JobCrudEndpointTests.Factory(); await global::SmartCv.Tests.Jobs.JobCrudEndpointTests.SeedAsync(factory); using var http = factory.CreateClient(); var api = new JobApiClient(http, NullLogger<JobApiClient>.Instance);
        using var create = new JobFormWorkspace(api, null); await create.LoadAsync(); create.Model.Title = "B21 test"; create.Model.Skills.Add(Skill("SQL", "SQL")); create.Model.Skills.Add(Skill("CSHARP", "C#"));
        var id = await create.SaveAsync(); Assert.NotNull(id);
        using (var scope = factory.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<SmartCv.Infrastructure.Data.SmartCvDbContext>(); db.EvaluationSkills.Add(new EvaluationSkill { EvaluationId = 123, JobId = id.Value, SkillCode = "CSHARP", IsMatched = true, Evidence = "retained" }); await db.SaveChangesAsync(); }
        using var edit = new JobFormWorkspace(api, id); await edit.LoadAsync(); var used = edit.Model.Skills.Single(s => s.SkillCode == "CSHARP"); used.Deleted = true; edit.Model.Title = "B21 updated"; edit.Model.Skills.Single(s => s.SkillCode == "SQL").Aliases = ["sql server", "database"];
        await edit.SaveAsync(); Assert.True(used.DeleteBlocked); Assert.False(used.Deleted); Assert.Equal(1, edit.PendingCount);
        var actual = await api.GetJobAsync(id.Value); Assert.Equal("B21 updated", actual.Data!.Title); Assert.Equal(2, actual.Data.Skills.Count); Assert.Contains("database", actual.Data.Skills.Single(s => s.SkillCode == "SQL").Aliases);
        edit.Model.Skills.Add(Skill("DELETE_ME", "Temporary", "NiceToHave")); await edit.SaveAsync(); var disposable = edit.Model.Skills.Single(s => s.SkillCode == "DELETE_ME"); disposable.Deleted = true; await edit.SaveAsync(); Assert.DoesNotContain(edit.Model.Skills, s => s.SkillCode == "DELETE_ME");
        using var check = factory.Services.CreateScope(); Assert.Equal("retained", (await check.ServiceProvider.GetRequiredService<SmartCv.Infrastructure.Data.SmartCvDbContext>().EvaluationSkills.SingleAsync()).Evidence);
    }
}
