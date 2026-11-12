using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Ai;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;
using Xunit.Abstractions;

namespace SmartCv.Tests;

public class LabelSuggestionEndpointTests(ITestOutputHelper output)
{
    private const string PrivateText = "Synthetic CV content must not be projected or logged by A36";
    private static readonly DateTime DecisionAt = new(2026,11,13,3,30,0,DateTimeKind.Utc);
    private sealed record Seed(int SetId, int OtherSetId, int JobId, int OtherJobId, int[] Apps);
    private sealed class NoAi : IAiEvaluator
    {
        public Task<AiEvaluationResult> EvaluateAsync(AiEvaluationRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A36 read-only endpoint must never call AI.");
    }
    private static WebApplicationFactory<Program> Factory(bool enabled = true) =>
        TestSupport.ApiFactory(settings: new() { ["CvUpload:Endpoint:Enabled"] = enabled.ToString(), ["Scoring:WorkerEnabled"] = "false" })
            .WithWebHostBuilder(b =>
            {
                b.ConfigureLogging(l => l.ClearProviders());
                b.ConfigureTestServices(s => { s.RemoveAll<IAiEvaluator>(); s.AddSingleton<IAiEvaluator>(new NoAi()); });
            });

    private static async Task<Seed> SeedAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        if (!db.Database.IsRelational()) await db.Database.EnsureCreatedAsync();
        var job = new Job { Title = "A36 synthetic backend" };
        var otherJob = new Job { Title = "A36 synthetic frontend" };
        var set = new EvaluationSet { Name = "A36 set" }; var otherSet = new EvaluationSet { Name = "A36 other set" };
        var decisions = new (string Stage,string? Reason)[]
        {
            ("Shortlisted",null),("Interview",null),("Rejected","MissingSkills"),("Rejected","NotEnoughExperience"),
            ("Rejected","OtherRequirement"),("Rejected","CandidateWithdrew"),("Rejected","PositionFilled"),("Rejected","Duplicate"),
            ("Reviewing",null),("OnHold",null),("New",null),("Shortlisted",null),("Interview",null),("Shortlisted",null)
        };
        var apps = decisions.Select((d,i) => new Application
        {
            Job = i == 12 ? otherJob : job, Stage = d.Stage, RejectReason = d.Reason, StageUpdatedAt = DecisionAt,
            Cv = new Cv { CvId = Guid.NewGuid(), OriginalFileName = $"a36-synthetic-{i}.pdf", StoredPath = "test-only", ContentType = "application/pdf",
                FileSizeBytes = 1, FileHash = Guid.NewGuid().ToString("N").PadRight(64,'0'), ExtractionStatus = i == 11 ? "Failed" : "Completed",
                ExtractedText = PrivateText, Candidate = i == 3 ? null : new Candidate { FullName = $"Synthetic {i}" } }
        }).ToArray();
        db.AddRange(apps); db.AddRange(set,otherSet); await db.SaveChangesAsync();
        HrLabel Label(int index, bool pass, EvaluationSet target, string actor, string? note = null) => new()
        { ApplicationId = apps[index].ApplicationId, EvaluationSetId = target.EvaluationSetId, IsPass = pass, LabeledBy = actor, Note = note, LabeledAt = DecisionAt.AddDays(-1) };
        db.HrLabels.AddRange(Label(1,true,set,"HR hiện tại","Ghi chú tiếng Việt – giữ nguyên"),Label(2,true,set,"HR trước","Nhãn Đạt vẫn phải giữ"),
            Label(4,false,set,"HR trước"),Label(12,false,set,"HR job khác"),Label(13,true,otherSet,"HR bộ khác","Không được lấy nhãn bộ khác"),
            Label(2,false,otherSet,"HR bộ khác"));
        db.EvaluationRuns.Add(new()
        {
            RunType = "PromptTest", EvaluationSetId = set.EvaluationSetId, PromptVersion = new() { VersionName = "A36 fixture", SystemPrompt = "Never called" },
            ModelName = "fake-not-called", KeywordWeight = .3m, AiWeight = .7m, Status = "Completed", TotalItems = 2, ProcessedItems = 2, AccuracyPercent = 50,
            Evaluations = [new() { ApplicationId = apps[1].ApplicationId, Status = "Completed", FinalScore = 10, PredictedPass = false },
                new() { ApplicationId = apps[2].ApplicationId, Status = "Completed", FinalScore = 99, PredictedPass = true }]
        });
        db.ApplicationEvents.Add(new() { ApplicationId = apps[1].ApplicationId, FromStage = "New", ToStage = "Interview", Actor = "Fixture HR", CreatedAt = DecisionAt });
        await db.SaveChangesAsync();
        return new(set.EvaluationSetId,otherSet.EvaluationSetId,job.JobId,otherJob.JobId,apps.Select(a=>a.ApplicationId).ToArray());
    }

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        return JsonSerializer.Serialize(new
        {
            Labels = await db.HrLabels.AsNoTracking().OrderBy(h=>h.EvaluationSetId).ThenBy(h=>h.ApplicationId)
                .Select(h=>new { h.EvaluationSetId,h.ApplicationId,h.IsPass,h.Note,h.LabeledBy,h.LabeledAt }).ToListAsync(),
            Stages = await db.Applications.AsNoTracking().OrderBy(a=>a.ApplicationId)
                .Select(a=>new { a.ApplicationId,a.Stage,a.RejectReason,a.StageUpdatedAt,a.StageVersion }).ToListAsync(),
            Runs = await db.EvaluationRuns.AsNoTracking().OrderBy(r=>r.RunId).Select(r=>new { r.RunId,r.Status,r.AccuracyPercent,r.ProcessedItems }).ToListAsync(),
            Evaluations = await db.Evaluations.AsNoTracking().OrderBy(e=>e.EvaluationId).Select(e=>new { e.EvaluationId,e.FinalScore,e.PredictedPass,e.Status }).ToListAsync(),
            Events = await db.ApplicationEvents.CountAsync()
        });
    }

    private static async Task VerifyAsync(HttpClient http, Seed seed)
    {
        var url = $"/api/evaluation-sets/{seed.SetId}/label-suggestions";
        var data = (await http.GetFromJsonAsync<LabelSuggestionResponse>(url))!;
        Assert.Equal(seed.SetId,data.EvaluationSetId); Assert.Null(data.JobId);
        Assert.Equal((3,2,2,5),(data.NewCount,data.SameCount,data.ConflictCount,data.SkippedCount)); Assert.Equal(7,data.Items.Count);
        Assert.Equal(new[] { "Conflict","Conflict","New","New","New","Same","Same" },data.Items.Select(i=>i.Status));
        var rejected = data.Items.Single(i=>i.ApplicationId==seed.Apps[2]);
        Assert.False(rejected.SuggestedIsPass); Assert.True(rejected.CurrentLabel); Assert.Equal("Nhãn Đạt vẫn phải giữ",rejected.CurrentNote);
        Assert.Equal("HR trước",rejected.CurrentLabeledBy); Assert.Contains("Thiếu kỹ năng bắt buộc",rejected.Reason);
        Assert.Contains("khác nhãn hiện tại Đạt",rejected.Reason); Assert.Equal(DecisionAt,rejected.StageUpdatedAt);
        Assert.Equal(DateTimeKind.Utc,rejected.StageUpdatedAt!.Value.Kind);
        var interview = data.Items.Single(i=>i.ApplicationId==seed.Apps[1]);
        Assert.True(interview.SuggestedIsPass); Assert.True(interview.CurrentLabel); Assert.Equal("Same",interview.Status);
        Assert.Equal("Ghi chú tiếng Việt – giữ nguyên",interview.CurrentNote);
        Assert.Null(data.Items.Single(i=>i.ApplicationId==seed.Apps[3]).CandidateName);
        var unrelated = data.Items.Single(i=>i.ApplicationId==seed.Apps[13]); Assert.Null(unrelated.CurrentLabel); Assert.Null(unrelated.CurrentNote); Assert.Null(unrelated.CurrentLabeledBy);
        foreach(var index in new[]{5,6,7,8,9,10,11})Assert.DoesNotContain(data.Items,i=>i.ApplicationId==seed.Apps[index]);
        var filtered = (await http.GetFromJsonAsync<LabelSuggestionResponse>(url+$"?jobId={seed.JobId}"))!;
        Assert.Equal((3,2,1,5),(filtered.NewCount,filtered.SameCount,filtered.ConflictCount,filtered.SkippedCount)); Assert.Equal(seed.JobId,filtered.JobId);
        Assert.All(filtered.Items,i=>Assert.Equal(seed.JobId,i.JobId));
        var otherJob = (await http.GetFromJsonAsync<LabelSuggestionResponse>(url+$"?jobId={seed.OtherJobId}"))!;
        var opposite = Assert.Single(otherJob.Items); Assert.True(opposite.SuggestedIsPass); Assert.False(opposite.CurrentLabel); Assert.Equal("Conflict",opposite.Status);
        Assert.Contains("khác nhãn hiện tại Không đạt",opposite.Reason);
        var unknownJob = (await http.GetFromJsonAsync<LabelSuggestionResponse>(url+"?jobId=999999"))!;
        Assert.Empty(unknownJob.Items); Assert.Equal((0,0,0,0),(unknownJob.NewCount,unknownJob.SameCount,unknownJob.ConflictCount,unknownJob.SkippedCount));
        var secondSet = (await http.GetFromJsonAsync<LabelSuggestionResponse>($"/api/evaluation-sets/{seed.OtherSetId}/label-suggestions"))!;
        Assert.Equal("Same",secondSet.Items.Single(i=>i.ApplicationId==seed.Apps[2]).Status);
        Assert.Equal("HR bộ khác",secondSet.Items.Single(i=>i.ApplicationId==seed.Apps[13]).CurrentLabeledBy);
        var body = await http.GetStringAsync(url); Assert.DoesNotContain(PrivateText,body); Assert.DoesNotContain("predictedPass",body,StringComparison.OrdinalIgnoreCase);
        using var missing = await http.GetAsync("/api/evaluation-sets/999999/label-suggestions"); Assert.Equal(HttpStatusCode.NotFound,missing.StatusCode);
        using var problem = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()); Assert.Equal(404,problem.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Suggestions_include_three_statuses_scope_labels_filter_jobs_and_ignore_AI_without_writing()
    {
        using var f = Factory(); using var http = f.CreateClient(); var seed = await SeedAsync(f); var before = await SnapshotAsync(f);
        await VerifyAsync(http,seed); await VerifyAsync(http,seed); await VerifyAsync(http,seed);
        Assert.Equal(before,await SnapshotAsync(f));
    }

    [Fact]
    public async Task Existing_set_without_candidates_returns_valid_empty_response_and_disabled_boundary_is_unchanged()
    {
        using var f = Factory(); using var http = f.CreateClient(); int setId;
        using(var scope=f.Services.CreateScope())
        {var db=scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();await db.Database.EnsureCreatedAsync();var set=new EvaluationSet{Name="Empty A36"};db.Add(set);await db.SaveChangesAsync();setId=set.EvaluationSetId;}
        var data=(await http.GetFromJsonAsync<LabelSuggestionResponse>($"/api/evaluation-sets/{setId}/label-suggestions"))!;
        Assert.Empty(data.Items);Assert.Equal((0,0,0,0),(data.NewCount,data.SameCount,data.ConflictCount,data.SkippedCount));
        using var disabled=Factory(false);using var closed=disabled.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound,(await closed.GetAsync($"/api/evaluation-sets/{setId}/label-suggestions")).StatusCode);
    }

    private sealed class ReadGuard : DbCommandInterceptor
    {
        public bool Enabled;
        public readonly List<string> Commands=[];
        private void Check(DbCommand command)
        {
            if(!Enabled)return;
            Assert.StartsWith("SELECT",command.CommandText.TrimStart(),StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ExtractedText",command.CommandText,StringComparison.OrdinalIgnoreCase);
            Commands.Add(command.CommandText);
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {Check(command);return base.ReaderExecutingAsync(command,eventData,result,ct);}
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData eventData,InterceptionResult<int> result,CancellationToken ct=default)
        {Check(command);return base.NonQueryExecutingAsync(command,eventData,result,ct);}
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,CommandEventData eventData,InterceptionResult<object> result,CancellationToken ct=default)
        {Check(command);return base.ScalarExecutingAsync(command,eventData,result,ct);}
    }

    [A36SqlFact]
    public async Task Isolated_SQL_Server_GET_is_read_only_two_queries_and_preserves_A16_accuracy_and_HR_decisions()
    {
        var sql=new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A36SqlFactAttribute.Setting));A36SqlFactAttribute.Validate(sql);
        sql.InitialCatalog="SmartCv_A36_Test_"+Guid.NewGuid().ToString("N");sql.Pooling=false;sql.ConnectTimeout=10;
        var evidence=TestSupport.RepoFile("artifacts/a36-tests/retained-sql-databases.txt");Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        await File.AppendAllTextAsync(evidence,sql.InitialCatalog+Environment.NewLine);
        var guard=new ReadGuard();
        using var f=Factory().WithWebHostBuilder(b=>b.ConfigureTestServices(services=>
        {
            services.RemoveAll<DbContextOptions<SmartCvDbContext>>();services.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
            services.AddDbContext<SmartCvDbContext>(o=>o.UseSqlServer(sql.ConnectionString).AddInterceptors(guard));
        }));
        using var http=f.CreateClient();
        using(var scope=f.Services.CreateScope())await scope.ServiceProvider.GetRequiredService<SmartCvDbContext>().Database.MigrateAsync();
        output.WriteLine("Retained A36 isolated database: "+sql.InitialCatalog);
        var seed=await SeedAsync(f);var before=await SnapshotAsync(f);guard.Enabled=true;
        await VerifyAsync(http,seed);await VerifyAsync(http,seed);
        foreach(var suffix in new[]{"",$"?jobId={seed.OtherJobId}"})
        {
            guard.Commands.Clear();using var response=await http.GetAsync($"/api/evaluation-sets/{seed.SetId}/label-suggestions{suffix}");
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal(2,guard.Commands.Count);
            output.WriteLine($"A36 GET {suffix}: 2 SELECTs; no CV text or writes.");
        }
        guard.Enabled=false;Assert.Equal(before,await SnapshotAsync(f));
        using(var scope=f.Services.CreateScope())Assert.Empty(scope.ServiceProvider.GetRequiredService<SmartCvDbContext>().ChangeTracker.Entries());
        output.WriteLine("Repeated GETs preserved labels (value/note/actor/time), A16 accuracy, scores, stage/version and history.");
    }
}

public sealed class A36SqlFactAttribute : FactAttribute
{
    public const string Setting="SMARTCV_A36_TEST_SQL_CONNECTION";
    public A36SqlFactAttribute()
    {
        var value=Environment.GetEnvironmentVariable(Setting);
        if(string.IsNullOrWhiteSpace(value))Skip=$"Set {Setting} to run a new retained A36 SQL test database.";
        else Validate(new SqlConnectionStringBuilder(value));
    }
    internal static void Validate(SqlConnectionStringBuilder sql)
    {
        if(!sql.InitialCatalog.StartsWith("SmartCv_A36_Test_",StringComparison.Ordinal)||!string.IsNullOrEmpty(sql.AttachDBFilename))
            throw new ArgumentException("Only SmartCv_A36_Test_ catalog names without AttachDBFilename are accepted.");
    }
}
