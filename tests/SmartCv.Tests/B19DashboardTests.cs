extern alias web;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Components.Dashboard;
using web::SmartCv.Web.Components.Pages;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

#pragma warning disable BL0006 // Dispatch actual Blazor component events without another package.
public class B19DashboardTests
{
    [Theory]
    [InlineData(null,null,null,true)]
    [InlineData("2","2024-02-29","2024-03-01",true)]
    [InlineData("0",null,null,false)]
    [InlineData("-1",null,null,false)]
    [InlineData("99999999999",null,null,false)]
    [InlineData("x",null,null,false)]
    [InlineData(" 2",null,null,false)]
    [InlineData(null,"2026-02-29",null,false)]
    [InlineData(null,"2026-1-01",null,false)]
    [InlineData(null,"2026-11-18","2026-11-17",false)]
    [InlineData(null,"0001-01-01",null,false)]
    [InlineData(null,null,"9999-12-31",false)]
    public void Filters_are_strict_and_invalid_input_never_becomes_all_data(string? job,string? from,string? to,bool valid)
    {
        var result=DashboardFilter.Parse(job,from,to);
        Assert.Equal(valid,result.Filter is not null);Assert.Equal(!valid,result.Error is not null);
    }

    [Theory]
    [InlineData("?jobId=1&jobId=2")]
    [InlineData("?from=2026-11-01&from=2026-11-02")]
    [InlineData("?to=2026-11-17&to=")]
    public void Duplicate_URL_filters_are_rejected(string query)=>Assert.Null(DashboardFilter.FromUri("https://fixture.test/"+query).Filter);

    [Fact]
    public void Query_round_trip_is_ISO_and_omits_empty_values()
    {
        var filter=new DashboardFilter(2,new(2024,2,29),new(2024,3,1));
        Assert.Equal("/dashboard?jobId=2&from=2024-02-29&to=2024-03-01",filter.Url);
        Assert.Equal(filter,DashboardFilter.FromUri("https://fixture.test"+filter.Url).Filter);
        Assert.Equal("/dashboard",new DashboardFilter().Url);
    }

    [Fact]
    public void Quick_ranges_are_inclusive_Vietnam_days_and_preserve_job()
    {
        var now=new DateTime(2026,11,16,18,0,0,DateTimeKind.Utc);
        Assert.Equal(new DashboardFilter(2,new(2026,11,11),new(2026,11,17)),new DashboardFilter(2).Quick(7,now));
        Assert.Equal(new DashboardFilter(2,new(2026,10,19),new(2026,11,17)),new DashboardFilter(2).Quick(30,now));
        Assert.Equal(new DashboardFilter(2),new DashboardFilter(2,new(2026,1,1)).Quick(null,now));
        var beforeMidnight=new DateTime(2026,11,16,16,59,0,DateTimeKind.Utc);
        Assert.Equal(new DateOnly(2026,11,16),new DashboardFilter().Quick(7,beforeMidnight).To);
        Assert.Equal(new DateOnly(2024,2,24),new DashboardFilter().Quick(7,new DateTime(2024,3,1,1,0,0,DateTimeKind.Utc)).From);
    }

    [Theory]
    [InlineData(null,"—")]
    [InlineData(-1d,"—")]
    [InlineData(0d,"0 phút")]
    [InlineData(.75,"45 phút")]
    [InlineData(5.5,"5,5 giờ")]
    [InlineData(55.2,"2,3 ngày")]
    public void Screening_duration_distinguishes_null_from_real_zero(double? hours,string expected)=>
        Assert.Equal(expected,DashboardText.Hours(hours is null?null:(decimal)hours));

    [Fact]
    public void Visual_widths_are_bounded_without_integer_overflow_and_counts_are_not_modified()
    {
        Assert.Equal("100",DashboardText.Width(int.MaxValue,int.MaxValue));Assert.Equal("0",DashboardText.Width(1,0));
        Assert.Equal("0",DashboardText.Width(0,20));Assert.Equal("100",DashboardText.Width(30,20));
        Assert.Equal("0.001",DashboardText.Width(1,100000));Assert.Equal("66,7%",DashboardText.Percent(66.7m));
    }

    [Fact]
    public void Flagged_link_prefers_selected_job_then_API_target_then_no_job()
    {
        Assert.Equal("/results?jobId=9&filter=flagged",DashboardText.FlaggedLink(new(){JobId=9,FlaggedJobId=2}));
        Assert.Equal("/results?jobId=2&filter=flagged",DashboardText.FlaggedLink(new(){FlaggedJobId=2}));
        Assert.Equal("/results?filter=flagged",DashboardText.FlaggedLink(new(){FlaggedJobId=0}));
    }

    private static DashboardSummaryDto Summary(DashboardFilter filter)=>new()
    {
        JobId=filter.JobId,From=filter.From,To=filter.To,TotalCvs=12,TotalApplications=20,FailedCvs=2,ActiveJobs=4,RunningRuns=3,
        FlaggedUnresolved=6,FlaggedJobId=1,RejectedCount=3,OnHoldCount=1,
        Funnel=[new(){Stage="New",Reached=20,Current=6},new(){Stage="Reviewing",Reached=14,Current=5,ConversionPercent=70},
            new(){Stage="Shortlisted",Reached=8,Current=3,ConversionPercent=57.1m},new(){Stage="Interview",Reached=4,Current=2,ConversionPercent=50}],
        ScreeningTime=new(){Count=14,MedianHours=5.5m,AverageHours=60},
        RejectReasons=[new(){Reason="MissingSkills",Count=2,Percent=66.7m,IsProfessional=true},new(){Reason="NotEnoughExperience",Count=1,Percent=33.3m,IsProfessional=true},
            new(){Reason="OtherRequirement"},new(){Reason="CandidateWithdrew"},new(){Reason="PositionFilled"},new(){Reason="Duplicate"}],
        RecentRuns=Enumerable.Range(11,6).Select(id=>Run(id,filter.JobId??1)).ToList()
    };
    private static RunSummaryDto Run(int id=11,int job=1)=>new()
    {
        RunId=id,RunType=id%2==0?"PromptTest":"Screening",Status="Completed",ModelName="fixture",TotalItems=20,ProcessedItems=20,FailedItems=2,
        CreatedAt=new DateTime(2026,11,17,1,30,0,DateTimeKind.Utc),Jobs=[new(){JobId=job,Title=job==1?"Backend Developer":"QA Engineer",Count=20}]
    };
    private sealed class Server:HttpMessageHandler
    {
        public ConcurrentQueue<string> Calls { get; }=new();
        public Func<DashboardFilter,DashboardSummaryDto> MakeSummary=Summary;
        public int? Failure;
        public bool Offline,Timeout;
        public TaskCompletionSource? Gate;
        public TaskCompletionSource Started { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get,request.Method);var path=request.RequestUri!.PathAndQuery;Calls.Enqueue(path);
            var query=QueryHelpers.ParseQuery(request.RequestUri.Query);
            if(request.RequestUri.AbsolutePath=="/api/jobs")return Json(new[]{new JobDto{JobId=1,Title="Backend Developer",IsActive=true},new JobDto{JobId=2,Title="QA Engineer",IsActive=false}});
            if(request.RequestUri.AbsolutePath=="/api/dashboard/summary")
            {
                var filter=DashboardFilter.FromUri(request.RequestUri.AbsoluteUri).Filter!;
                if(filter.JobId==1 && Gate is not null){using var registration=ct.Register(()=>Canceled.TrySetResult());Started.TrySetResult();await Gate.Task;}
                if(Offline)throw new HttpRequestException("PRIVATE network details");
                if(Timeout)throw new TaskCanceledException("PRIVATE timeout details");
                if(Failure is {} code)return Json(new{detail="PRIVATE SqlException or candidate data"},code);
                return Json(MakeSummary(filter));
            }
            var job=int.TryParse(query.GetValueOrDefault("jobId"),out var jid)?jid:1;
            if(request.RequestUri.AbsolutePath=="/api/runs/latest")return Json(Run(101,job));
            if(request.RequestUri.AbsolutePath=="/api/runs")return Json(new[]{Run(101,1),Run(102,2)});
            if(request.RequestUri.AbsolutePath.EndsWith("/ranking"))return Json(Ranking(job));
            if(request.RequestUri.AbsolutePath is "/api/runs/101" or "/api/runs/102")
                return Json(new RunProgressDto(101,"Screening","Completed",1,"fixture","fixture",.2m,.3m,.7m,70,2,2,0,100,new(2026,11,17),new(2026,11,17)));
            return Json(new{detail="Fixture route missing"},404);
        }
        public static RankingResponse Ranking(int job)=>new()
        {
            RunId=101,RunStatus="Completed",PromptVersionName="fixture",JobId=job,JobTitle="Fixture job",PassThreshold=70,FlaggedCount=1,
            Jobs=[new(){JobId=1,Title="Backend Developer",Count=2},new(){JobId=2,Title="QA Engineer",Count=2}],
            Items=[Row(1,true),Row(2,false)]
        };
        private static RankingItemDto Row(int id,bool flagged)=>new()
        {
            EvaluationId=id,ApplicationId=id,Rank=id,CandidateName="PRIVATE candidate "+id,FileName="PRIVATE-file.pdf",Status="Completed",FinalScore=80,KeywordScore=80,AiScore=80,ScoreSource="Combined",FitLabel="Phù hợp",PredictedPass=true,
            ReviewFlags=flagged?[new(){Code="ScoreDisagreement",Message="Cần xem kỹ · fixture"}]:[]
        };
        private static HttpResponseMessage Json(object value,int status=200)=>new((HttpStatusCode)status){Content=JsonContent.Create(value,value.GetType())};
    }
    private static DashboardApiClient DashboardApi(Server server)=>new(new HttpClient(server){BaseAddress=new Uri("https://api.test/")},NullLogger<DashboardApiClient>.Instance);
    private static JobApiClient JobsApi(Server server)=>new(new HttpClient(server){BaseAddress=new Uri("https://api.test/")},NullLogger<JobApiClient>.Instance);

    [Fact]
    public async Task Services_use_A19_and_load_inactive_jobs_without_writes()
    {
        var server=new Server();using var state=new DashboardWorkspace(DashboardApi(server),JobsApi(server));
        await Task.WhenAll(state.LoadJobsAsync(),state.LoadAsync(new(2,new(2026,11,1),new(2026,11,17))));
        Assert.Contains("/api/jobs?activeOnly=false",server.Calls);
        Assert.Contains("/api/dashboard/summary?jobId=2&from=2026-11-01&to=2026-11-17",server.Calls);
        Assert.Contains(state.Jobs,j=>!j.IsActive);Assert.Equal(2,state.Data!.JobId);
    }
    [Theory]
    [InlineData(400)] [InlineData(404)] [InlineData(429)] [InlineData(500)]
    public async Task API_failures_preserve_same_scope_data_without_exposing_details(int code)
    {
        var server=new Server();using var state=new DashboardWorkspace(DashboardApi(server),JobsApi(server));
        await state.LoadAsync(new(2));var previous=state.Data;server.Failure=code;await state.LoadAsync(new(2));
        Assert.Same(previous,state.Data);Assert.True(state.ShowData);Assert.NotNull(state.Error);Assert.DoesNotContain("PRIVATE",state.Error);
        await state.LoadAsync(new(1));Assert.False(state.ShowData);Assert.Equal(1,state.Filter!.JobId);
    }
    [Theory]
    [InlineData(true,false)] [InlineData(false,true)]
    public async Task Network_and_timeout_errors_are_recoverable(bool offline,bool timeout)
    {
        var server=new Server{Offline=offline,Timeout=timeout};using var state=new DashboardWorkspace(DashboardApi(server),JobsApi(server));
        await state.LoadAsync(new());Assert.NotNull(state.Error);Assert.False(state.Loading);Assert.False(state.ShowData);
        server.Offline=server.Timeout=false;await state.LoadAsync(new());Assert.Null(state.Error);Assert.True(state.ShowData);
    }
    [Fact]
    public async Task Mismatched_response_is_rejected_instead_of_showing_other_scope()
    {
        var server=new Server{MakeSummary=_=>Summary(new(9))};using var state=new DashboardWorkspace(DashboardApi(server),JobsApi(server));
        await state.LoadAsync(new(1));Assert.Null(state.Data);Assert.Contains("không khớp",state.Error);
    }
    [Fact]
    public async Task Slow_old_response_is_canceled_and_cannot_replace_new_filter()
    {
        var server=new Server{Gate=new()};using var state=new DashboardWorkspace(DashboardApi(server),JobsApi(server));
        var first=state.LoadAsync(new(1));await server.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.LoadAsync(new(2));await server.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2,state.Data!.JobId);server.Gate.SetResult();await first;Assert.Equal(2,state.Data.JobId);
    }

    private sealed class Nav:NavigationManager
    {
        public Nav(string uri="https://app.test/"){Initialize("https://app.test/",uri);}
        protected override void NavigateToCore(string uri,bool forceLoad){Uri=ToAbsoluteUri(uri).AbsoluteUri;NotifyLocationChanged(false);}
        protected override void NavigateToCore(string uri,NavigationOptions options)=>NavigateToCore(uri,options.ForceLoad);
    }
    private sealed class Js:IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier,object?[]? args)=>ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier,CancellationToken ct,object?[]? args)=>ValueTask.FromResult(default(TValue)!);
    }
    private static ServiceProvider Services(Server server,Nav nav)
    {
        var services=new ServiceCollection().AddLogging().AddSingleton<NavigationManager>(nav).AddSingleton(DashboardApi(server)).AddSingleton(JobsApi(server));
        services.AddSingleton(new RunApiClient(new HttpClient(server){BaseAddress=new Uri("https://api.test/")},NullLogger<RunApiClient>.Instance));
        services.AddSingleton(new ApplicationApiClient(new HttpClient(server){BaseAddress=new Uri("https://api.test/")},NullLogger<ApplicationApiClient>.Instance));
        services.AddSingleton(new AnonymityState());services.AddSingleton(new HrActorState());services.AddSingleton(B43UiTestSupport.Session());services.AddSingleton(new WorkspaceApiClient(new HttpClient(server){BaseAddress=new Uri("https://api.test/")},NullLogger<WorkspaceApiClient>.Instance));services.AddSingleton<IJSRuntime>(new Js());services.AddSupplyValueFromQueryProvider(); // B56: Home reads the verified session and workspace quota
        return services.BuildServiceProvider();
    }
    private static async Task<string> Html<T>(ServiceProvider services,Dictionary<string,object?>? parameters=null) where T:IComponent
    {
        await using var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async()=>WebUtility.HtmlDecode((await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters??[]))).ToHtmlString()));
    }

    [Fact]
    public async Task Dashboard_renders_exactly_five_API_KPIs_charts_times_and_five_recent_runs()
    {
        var server=new Server();using var services=Services(server,new());var html=await Html<Home>(services);
        Assert.Equal(5,System.Text.RegularExpressions.Regex.Matches(html,"data-kpi=").Count);
        Assert.Contains("12</strong>",html);Assert.Contains("20 hồ sơ ứng tuyển",html);Assert.Contains("/results?jobId=1&filter=flagged",html);
        Assert.Contains("/jobs",html);Assert.Contains("/runs?status=Running",html);Assert.Contains("5,5 giờ",html);Assert.Contains("2,5 ngày",html);
        Assert.Contains("66,7%",html);Assert.Contains("57,1%",html);Assert.Contains("17/11/2026 08:30",html);
        Assert.Contains("/runs/15",html);Assert.DoesNotContain("/runs/16",html);Assert.DoesNotContain("PRIVATE",html);
    }
    [Fact]
    public async Task Full_empty_and_filtered_empty_are_different_and_null_median_is_not_zero()
    {
        var server=new Server{MakeSummary=f=>new(){JobId=f.JobId,From=f.From,To=f.To}};
        using(var sp=Services(server,new())){var html=await Html<Home>(sp);Assert.Contains("Chưa có hồ sơ nào",html);Assert.Contains("/upload",html);Assert.DoesNotContain("Không có hồ sơ trong khoảng",html);}
        using(var sp=Services(server,new("https://app.test/?from=2026-11-01")))
        {var html=await Html<Home>(sp);Assert.Contains("Không có hồ sơ trong khoảng đã chọn",html);Assert.Contains("Chưa đủ dữ liệu",html);Assert.DoesNotContain("0 phút",html);Assert.DoesNotContain("onboarding-title",html);}
    }
    [Theory]
    [InlineData("?jobId=x")][InlineData("?from=2026-02-30")][InlineData("?jobId=1&jobId=2")]
    public async Task Bad_deep_links_show_filter_error_and_do_not_request_a_summary(string query)
    {
        var server=new Server();using var sp=Services(server,new("https://app.test/"+query));var html=await Html<Home>(sp);
        Assert.Contains("Chưa áp dụng bộ lọc này",html);Assert.DoesNotContain(server.Calls,p=>p.Contains("/dashboard/summary"));
    }
    [Fact]
    public async Task Chart_preserves_reached_current_and_API_conversion_with_empty_reason_safety()
    {
        using var sp=Services(new(),new());var data=Summary(new());
        var html=await Html<StageFunnel>(sp,new(){["Steps"]=data.Funnel,["Total"]=20,["Rejected"]=3,["OnHold"]=1});
        Assert.Contains(">20</b> đã tới",html);Assert.Contains(">6</b> đang ở",html);Assert.Contains("57,1%",html);Assert.Contains("Loại",html);Assert.Contains("Dự phòng",html);
        var empty=await Html<RejectReasonChart>(sp,new(){["Reasons"]=data.RejectReasons,["Total"]=0});Assert.Contains("Chưa có hồ sơ nào bị loại",empty);Assert.DoesNotContain("66,7%",empty);
        var zeroProgress=await Html<StageFunnel>(sp,new(){["Total"]=1,["Steps"]=new List<FunnelStepDto>{new(){Stage="New",Reached=1,Current=1},new(){Stage="Reviewing",ConversionPercent=0},new(){Stage="Shortlisted"},new(){Stage="Interview"}}});
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(zeroProgress,"Điểm bắt đầu"));
        Assert.Contains("Chuyển từ bước trước: —",zeroProgress);Assert.Contains("Chuyển từ bước trước: 0%",zeroProgress);
    }

    private sealed class Events(IServiceProvider services):Renderer(services,NullLoggerFactory.Instance)
    {
        protected override RendererInfo RendererInfo { get; }=new("B19-test",isInteractive:false);
        public override Dispatcher Dispatcher { get; }=Dispatcher.CreateDefault();private int root=-1;
        protected override Task UpdateDisplayAsync(in RenderBatch batch)=>Task.CompletedTask;
        protected override void HandleException(Exception error)=>System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        public Task Show<T>() where T:IComponent=>Dispatcher.InvokeAsync(async()=>{root=AssignRootComponentId(InstantiateComponent(typeof(T)));await RenderRootComponentAsync(root);});
        private IEnumerable<RenderTreeFrame[]> Components(int id)
        {
            var frames=GetCurrentRenderTreeFrames(id);var array=frames.Array.Take(frames.Count).ToArray();yield return array;
            foreach(var f in array.Where(f=>f.FrameType==RenderTreeFrameType.Component))foreach(var child in Components(f.ComponentId))yield return child;
        }
        private static string? Attr(RenderTreeFrame[] f,int i,string name)=>f.Skip(i+1).TakeWhile(a=>a.FrameType==RenderTreeFrameType.Attribute).FirstOrDefault(a=>a.AttributeName==name).AttributeValue?.ToString();
        public Task ClickId(string id)=>Dispatcher.InvokeAsync(async()=>
        {
            foreach(var f in Components(root))for(var i=0;i<f.Length;i++)if(f[i].FrameType==RenderTreeFrameType.Element&&Attr(f,i,"id")==id)
            {var e=f.Skip(i+1).TakeWhile(a=>a.FrameType==RenderTreeFrameType.Attribute).Single(a=>a.AttributeName=="onclick");await DispatchEventAsync(e.AttributeEventHandlerId,null,new MouseEventArgs());return;}
            throw new InvalidOperationException("Missing button "+id);
        });
        public Task<string?> Attribute(string id,string name)=>Dispatcher.InvokeAsync(()=>Components(root).SelectMany(f=>Enumerable.Range(0,f.Length).Where(i=>f[i].FrameType==RenderTreeFrameType.Element&&Attr(f,i,"id")==id).Select(i=>Attr(f,i,name))).FirstOrDefault());
        public Task<string> Text()=>Dispatcher.InvokeAsync(()=>string.Join(" ",Components(root).SelectMany(f=>f).Where(f=>f.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup).Select(f=>f.FrameType==RenderTreeFrameType.Text?f.TextContent:f.MarkupContent)));
        public Task Navigate(Nav nav,string uri)=>Dispatcher.InvokeAsync(()=>nav.NavigateTo(uri));
    }
    [Fact]
    public async Task Home_handles_back_forward_and_same_query_without_duplicate_requests()
    {
        var server=new Server();var nav=new Nav("https://app.test/dashboard");using var sp=Services(server,nav);await using var renderer=new Events(sp);await renderer.Show<Home>();
        Assert.Single(server.Calls,p=>p.StartsWith("/api/dashboard/summary"));
        await renderer.Navigate(nav,"/dashboard?jobId=2&from=2026-11-01");Assert.Equal("2",await renderer.Attribute("dashboard-job","value"));
        await renderer.Navigate(nav,"/dashboard");Assert.Equal("",await renderer.Attribute("dashboard-job","value"));
        Assert.Equal(3,server.Calls.Count(p=>p.StartsWith("/api/dashboard/summary")));
    }
    [Fact]
    public async Task Flagged_deep_link_uses_B7_latest_job_and_removal_survives_renders_and_F5()
    {
        var server=new Server();var nav=new Nav("https://app.test/results?jobId=2&filter=flagged");using var sp=Services(server,nav);await using var renderer=new Events(sp);await renderer.Show<Results>();
        Assert.Contains("/api/runs/latest?jobId=2",server.Calls);Assert.Equal("true",await renderer.Attribute("flag-filter","aria-pressed"));
        Assert.DoesNotContain("PRIVATE",await renderer.Text());var before=server.Calls.Count;
        await renderer.ClickId("flag-filter");Assert.DoesNotContain("filter=",nav.Uri);Assert.Equal("false",await renderer.Attribute("flag-filter","aria-pressed"));
        Assert.Equal(before,server.Calls.Count);await renderer.Navigate(nav,"/results?jobId=2&filter=flagged");Assert.Equal("true",await renderer.Attribute("flag-filter","aria-pressed"));
        await renderer.ClickId("flag-filter");using var reloadSp=Services(server,new(nav.Uri));await using var reload=new Events(reloadSp);await reload.Show<Results>();
        Assert.Equal("false",await reload.Attribute("flag-filter","aria-pressed"));
    }
}
#pragma warning restore BL0006
