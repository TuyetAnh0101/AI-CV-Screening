extern alias web;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;
using web::SmartCv.Web.Components.Prompts;

namespace SmartCv.Tests;
#pragma warning disable BL0006
public class AccuracyComparisonTests
{
    private static PromptTestRunDto Run(int id, int version, int correct = 8, int missed = 1, int wrong = 1, int unscored = 0, int set = 1, string status = "Completed", decimal? accuracy = 80, int total = 10, DateTime? completed = null) => new()
    { RunId = id, PromptVersionId = version, PromptVersionName = $"v{version}", EvaluationSetId = set, Status = status, AccuracyPercent = accuracy, TotalItems = 12, TotalLabeledCount = total, CorrectCount = correct, MissedCount = missed, WrongPickCount = wrong, UnscoredCount = unscored, KeywordOnlyCount = 0, ModelName = "fixture", CreatedAt = new(2026,11,1), CompletedAt = completed ?? new DateTime(2026,11,10,2,0,0,DateTimeKind.Utc) };
    private static PromptTestSummaryDto Summary(PromptTestRunDto r) => new()
    { RunId = r.RunId, PromptVersionId = r.PromptVersionId, EvaluationSetId = r.EvaluationSetId, AccuracyPercent = r.AccuracyPercent, CorrectCount = r.CorrectCount!.Value, MissedCount = r.MissedCount!.Value, WrongPickCount = r.WrongPickCount!.Value };
    [Theory]
    [InlineData("Running",80,8,10,false)] [InlineData("Failed",80,8,10,false)] [InlineData("Completed",null,8,10,false)]
    [InlineData("Completed",101,8,10,false)] [InlineData("Completed",-1,8,10,false)] [InlineData("Completed",0,0,0,false)]
    [InlineData("Completed",79,8,10,false)] [InlineData("Completed",80,8,10,true)]
    public void Invalid_results_are_not_ranked(string status, int? accuracy, int correct, int total, bool expected) => Assert.Equal(expected, AccuracyComparisonView.Valid(Run(1,1, correct:correct, status:status, accuracy:accuracy,total:total)));
    [Fact]
    public void Latest_uses_completion_time_then_run_id_and_never_mixes_sets()
    {
        var old = Run(100,1, completed:new DateTime(2026,11,1));
        var current = Run(4,1); var tie = Run(5,1);
        var rows = AccuracyComparisonView.Latest([old,current,tie,Run(6,1,status:"Running"),Run(7,2,set:2)],1);
        Assert.Equal(5, Assert.Single(rows).RunId);
    }
    [Fact]
    public void Best_requires_verification_and_ties_prefer_fewer_missed_then_id()
    {
        var a=Run(1,1,missed:2,wrong:0); var b=Run(2,2); var c=Run(3,3);
        Assert.Null(AccuracyComparisonView.Best([a,b],false));
        Assert.Equal(2, AccuracyComparisonView.Best([c,a,b],true)!.RunId);
        Assert.Null(AccuracyComparisonView.Best([a],true));
        Assert.Null(AccuracyComparisonView.Best([a,Run(4,4,wrong:0,unscored:1)],true));
    }
    [Fact]
    public void Error_cv_stays_in_denominator_not_in_missed_count()
    {
        var r=Run(1,1,correct:7,missed:1,wrong:1,unscored:1,accuracy:70);
        Assert.True(AccuracyComparisonView.Valid(r)); Assert.Equal(1,r.MissedCount);
        Assert.False(AccuracyComparisonView.Matches(r,Summary(Run(1,1))));
    }
    private sealed class Server : HttpMessageHandler
    {
        public List<PromptTestRunDto> Runs = [Run(1,1,7,1,2,accuracy:70),Run(2,2)];
        public bool Fair = true, Fail, Stale;
        public int Gets;
        public List<string> Paths=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get,req.Method); Paths.Add(req.RequestUri!.PathAndQuery);
            object result;
            if(Fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable){Content=JsonContent.Create(new{detail="API tạm mất kết nối"})});
            if(req.RequestUri.AbsolutePath=="/api/evaluation-sets") result=new[]{new EvaluationSetDto{EvaluationSetId=1,Name="Bộ HR",LabelCount=10}};
            else if(req.RequestUri.AbsolutePath.EndsWith("/compare"))
            {
                var query=Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(req.RequestUri.Query);
                var a=Runs.Single(r=>r.RunId==int.Parse(query["baseRunId"].ToString()));
                var b=Runs.Single(r=>r.RunId==int.Parse(query["candidateRunId"].ToString()));
                result=new PromptCompareDto{Base=Summary(a),Candidate=Summary(Stale?Run(b.RunId,b.PromptVersionId,7,1,2,accuracy:70):b),IsFairComparison=Fair,Verdict=Fair?"Better":"NotComparable"};
            }
            else {Gets++;result=Runs;}
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(result)});
        }
    }
    private static ServiceProvider Services(Server s) => new ServiceCollection().AddLogging().AddSingleton(new PromptApiClient(new HttpClient(s){BaseAddress=new Uri("http://fixture.test/")},NullLogger<PromptApiClient>.Instance)).BuildServiceProvider();
    private static readonly List<PromptVersionDto> Versions=[new(){PromptVersionId=1,VersionName="v1",IsActive=true},new(){PromptVersionId=2,VersionName="v2"}];
    private static async Task<string> Render(Server s)
    {
        using var sp=Services(s);await using var renderer=new HtmlRenderer(sp,sp.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async()=>WebUtility.HtmlDecode((await renderer.RenderComponentAsync<AccuracyCompare>(ParameterView.FromDictionary(new Dictionary<string,object?>{{"Versions",Versions}}))).ToHtmlString()));
    }
    [Fact]
    public async Task Component_shows_verified_best_real_counters_active_badge_and_B16_action()
    {
        var s=new Server();var html=await Render(s);
        Assert.Contains("80%",html);Assert.Contains("7/10",html);Assert.Contains("8/10",html);Assert.Contains("Đang dùng",html);
        Assert.Contains("CAO NHẤT",html);Assert.Contains("best-fill",html);Assert.Contains("Xem CV lệch",html);
        Assert.Contains(s.Paths,p=>p.Contains("evaluationSetId=1"));
        Assert.Contains("10/11/2026 09:00",html);
    }
    [Theory]
    [InlineData(false,false)] [InlineData(true,true)]
    public async Task Unfair_or_changed_data_does_not_publish_best(bool fair,bool stale)
    {var html=await Render(new(){Fair=fair,Stale=stale});Assert.DoesNotContain("CAO NHẤT",html);Assert.Contains("Không xếp hạng",html);}
    [Fact]
    public async Task Empty_and_failure_are_explicit_not_zero_percent()
    {Assert.Contains("Chưa có lần đánh giá",await Render(new(){Runs=[]}));Assert.Contains("API tạm mất kết nối",await Render(new(){Fail=true}));}
    private sealed class TestRenderer(IServiceProvider sp):Renderer(sp,NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher{get;}=Dispatcher.CreateDefault();
        protected override Task UpdateDisplayAsync(in RenderBatch b)=>Task.CompletedTask;
        protected override void HandleException(Exception e)=>System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e);
        private int root = -1;
        public Task ShowAsync(int refresh)=>Dispatcher.InvokeAsync(async()=>{if(root<0) root=AssignRootComponentId(InstantiateComponent(typeof(AccuracyCompare)));await RenderRootComponentAsync(root,ParameterView.FromDictionary(new Dictionary<string,object?>{{"Versions",Versions},{"RefreshVersion",refresh}}));});
        public Task PickAsync(int runId, Action<int> selected) => Dispatcher.InvokeAsync(async () =>
        {
            if (root < 0) root = AssignRootComponentId(InstantiateComponent(typeof(AccuracyCompare)));
            await RenderRootComponentAsync(root, ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["Versions"] = Versions, ["OnPick"] = EventCallback.Factory.Create<int>(this, selected) }));
            var frames = GetCurrentRenderTreeFrames(root);
            for (var i = 0; i < frames.Count; i++)
            {
                if (frames.Array[i].FrameType != RenderTreeFrameType.Element || frames.Array[i].ElementName != "button") continue;
                var attrs = frames.Array.Skip(i + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute).ToList();
                if (!attrs.Any(f => f.AttributeName == "aria-label" && f.AttributeValue?.ToString()?.StartsWith($"Xem CV lệch lượt #{runId} ·") == true)) continue;
                await DispatchEventAsync(attrs.Single(f => f.AttributeName == "onclick").AttributeEventHandlerId, null, new MouseEventArgs());
                return;
            }
            throw new InvalidOperationException("Missing B16 action");
        });
    }
    [Fact]
    public async Task Completion_signal_refreshes_without_polling_or_post()
    {
        var s=new Server();using var sp=Services(s);await using var r=new TestRenderer(sp);
        await r.ShowAsync(0);Assert.Equal(1,s.Gets);
        s.Runs.Add(Run(3,3,9,0,1,accuracy:90));await r.ShowAsync(1);Assert.Equal(2,s.Gets);
        await r.ShowAsync(1);Assert.Equal(2,s.Gets);
    }
    [Fact]
    public async Task Mismatch_action_emits_run_id_instead_of_prompt_version_id()
    {
        var server = new Server { Runs = [Run(41, 1)] };
        using var services = Services(server); await using var renderer = new TestRenderer(services);
        var selected = 0; await renderer.PickAsync(41, id => selected = id);
        Assert.Equal(41, selected);
    }
}
#pragma warning restore BL0006
