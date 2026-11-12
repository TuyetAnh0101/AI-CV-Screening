extern alias web;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Components.Results;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;
#pragma warning disable BL0006
public class B35DecisionTests
{
    private sealed class Api : HttpMessageHandler
    {
        public string Stage = "New";
        public string? Reason;
        public Guid Version = Guid.NewGuid();
        public int Status = 200, Writes;
        public int ReadStatus = 200, HistoryStatus = 200;
        public ChangeStageRequest? Request;
        public List<ApplicationEventDto> Events = [];
        public EvaluationDetailDto Detail() => new() { EvaluationId = 22, ApplicationId = 91, Stage = Stage, RejectReason = Reason, StageVersion = Version };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (r.Method == HttpMethod.Patch)
            {
                Assert.Equal("/api/applications/91/stage", r.RequestUri!.AbsolutePath);
                Writes++; Request = await r.Content!.ReadFromJsonAsync<ChangeStageRequest>(ct);
                if (Status == 0) throw new HttpRequestException("Synthetic offline");
                if (Status == -1) throw new TaskCanceledException("Synthetic timeout");
                if (Status == 409) { Stage = "Interview"; Version = Guid.NewGuid(); }
                if (Status != 200) return new((HttpStatusCode)Status);
                var before = Stage; Stage = Request!.ToStage; Reason = Request.RejectReason; Version = Guid.NewGuid();
                Events.Add(new() { ApplicationId = 91, EventId = Events.Count + 1, FromStage = before, ToStage = Stage, RejectReason = Reason,
                    Actor = Request.Actor, Note = Request.Note, CreatedAt = new DateTime(2026,11,12,3,0,0,DateTimeKind.Utc) });
                return Json(new StageChangeResultDto { ApplicationId = 91, Stage = Stage, StageVersion = Version, Outcome = "Changed" });
            }
            Assert.Contains(r.RequestUri!.AbsolutePath, new[] { "/api/evaluations/22", "/api/applications/91/events" });
            var readStatus = r.RequestUri.AbsolutePath.EndsWith("events") ? HistoryStatus : ReadStatus;
            if (readStatus != 200) return new((HttpStatusCode)readStatus);
            return r.RequestUri.AbsolutePath.EndsWith("events") ? Json(Events) : Json(Detail());
        }
        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
    private sealed class Live(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override RendererInfo RendererInfo { get; } = new("Server", true);
        protected override void HandleException(Exception exception) => throw exception;
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        private int root;
        private HrDecisionPanel panel = null!;
        public Task Start(EvaluationDetailDto d) => Dispatcher.InvokeAsync(async () => { panel = (HrDecisionPanel)InstantiateComponent(typeof(HrDecisionPanel)); root = AssignRootComponentId(panel); await RenderRootComponentAsync(root, ParameterView.FromDictionary(new Dictionary<string,object?> { ["Detail"] = d })); });
        public Task Edit(string stage, string reason = "", string note = "") => Dispatcher.InvokeAsync(() =>
        {
            foreach (var (key,value) in new[] { ("stage",stage),("reason",reason),("note",note),("actor","HR – Tuyết Ánh") })
                typeof(HrDecisionPanel).GetField(key,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(panel,value);
        });
        public bool CanSave => Dispatcher.InvokeAsync(() => (bool)typeof(HrDecisionPanel).GetProperty("CanSave",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(panel)!).Result;
        public Task Save() => Dispatcher.InvokeAsync(async () => { await (Task)typeof(HrDecisionPanel).GetMethod("SaveAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(panel,null)!; await RenderRootComponentAsync(root); });
        public Task Retry() => Dispatcher.InvokeAsync(async () => { await (Task)typeof(HrDecisionPanel).GetMethod("RetryAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(panel,null)!; await RenderRootComponentAsync(root); });
        public Task Cancel() => Dispatcher.InvokeAsync(() => { typeof(HrDecisionPanel).GetMethod("Reset",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(panel,null); });
        private IEnumerable<RenderTreeFrame> Frames(int id) { var f=GetCurrentRenderTreeFrames(id); return f.Array.Take(f.Count).SelectMany(x=>x.FrameType==RenderTreeFrameType.Component?Frames(x.ComponentId):[x]); }
        public string Text => Dispatcher.InvokeAsync(()=>string.Concat(Frames(root).Select(f=>f.FrameType==RenderTreeFrameType.Text?f.TextContent:f.FrameType==RenderTreeFrameType.Markup?f.MarkupContent:""))).Result;
    }
    private static ServiceProvider Services(Api api)
    {
        var http = new HttpClient(api) { BaseAddress = new Uri("https://api.test/") };
        return new ServiceCollection().AddLogging().AddSingleton(new HrActorState())
            .AddSingleton(new ApplicationApiClient(http,NullLogger<ApplicationApiClient>.Instance))
            .AddSingleton(new EvaluationApiClient(http,NullLogger<EvaluationApiClient>.Instance)).BuildServiceProvider();
    }
    [Fact]
    public async Task Validation_cancel_and_noop_never_write()
    {
        var api=new Api();using var sp=Services(api);await using var r=new Live(sp);await r.Start(api.Detail());
        Assert.Contains("Chưa có quyết định HR",r.Text);
        await r.Edit("New",note:"Chỉ sửa ghi chú");Assert.False(r.CanSave);await r.Save();
        await r.Edit("Rejected");Assert.False(r.CanSave);await r.Save();
        await r.Edit("Rejected","MissingSkills",new string('Á',501));Assert.False(r.CanSave);
        await r.Edit("Reviewing");Assert.True(r.CanSave);await r.Cancel();Assert.False(r.CanSave);await r.Save();Assert.Equal(0,api.Writes);
    }
    [Fact]
    public async Task Save_uses_application_identity_tokens_and_real_history()
    {
        var api=new Api();var version=api.Version;using var sp=Services(api);await using var r=new Live(sp);await r.Start(api.Detail());
        var note=new string('Á',500);await r.Edit("Rejected","MissingSkills",note);await r.Save();
        Assert.Equal(1,api.Writes);Assert.Equal("New",api.Request!.ExpectedStage);Assert.Equal(version,api.Request.ExpectedStageVersion);
        Assert.Equal(note,api.Events.Single().Note);Assert.Contains("Đã lưu quyết định",r.Text);Assert.Contains("giờ Việt Nam",r.Text);Assert.False(r.CanSave);
        await r.Edit("Reviewing");await r.Save();Assert.Null(api.Request.RejectReason);Assert.Equal(2,api.Events.Count);
        Assert.True(r.Text.IndexOf("Đang xem",StringComparison.Ordinal)>=0);
    }
    [Theory]
    [InlineData(409)] [InlineData(400)] [InlineData(404)] [InlineData(500)] [InlineData(0)] [InlineData(-1)]
    public async Task Failures_never_retry_or_invent_events(int status)
    {
        var api=new Api { Status=status };using var sp=Services(api);await using var r=new Live(sp);await r.Start(api.Detail());
        await r.Edit("Shortlisted");await r.Save();Assert.Equal(1,api.Writes);Assert.Empty(api.Events);
        if(status==409) { Assert.Contains("HR khác",r.Text);Assert.Contains("Phỏng vấn",r.Text);Assert.False(r.CanSave); }
        else Assert.DoesNotContain("Đã lưu quyết định HR.",r.Text);
    }
    [Fact]
    public async Task History_failure_blocks_writes_and_does_not_claim_empty_history()
    {
        var api=new Api { HistoryStatus=500 };using var sp=Services(api);await using var r=new Live(sp);await r.Start(api.Detail());
        await r.Edit("Reviewing");Assert.False(r.CanSave);await r.Save();Assert.Equal(0,api.Writes);
        Assert.Contains("Không tải được lịch sử",r.Text);Assert.DoesNotContain("Chưa có quyết định HR",r.Text);
    }
    [Fact]
    public async Task Failed_resynchronization_blocks_additional_writes()
    {
        var api=new Api();using var sp=Services(api);await using var r=new Live(sp);await r.Start(api.Detail());
        api.ReadStatus=500;await r.Edit("Reviewing");await r.Save();Assert.Equal(1,api.Writes);
        Assert.Contains("Chưa đồng bộ",r.Text);await r.Edit("Interview");Assert.False(r.CanSave);await r.Save();Assert.Equal(1,api.Writes);
        api.ReadStatus=200;await r.Retry();Assert.Contains("Đã tải lại quyết định",r.Text);Assert.DoesNotContain("Chưa đồng bộ",r.Text);
        await r.Edit("Interview");Assert.True(r.CanSave);Assert.Equal(1,api.Writes);
    }
    [Fact]
    public async Task Timeline_orders_latest_then_event_id_in_Vietnam_time()
    {
        var api=new Api();var date=new DateTime(2026,11,12,3,0,0,DateTimeKind.Utc);
        api.Events=[new(){ApplicationId=91,EventId=1,FromStage="New",ToStage="Reviewing",CreatedAt=date,Actor="HR",Note="old-marker"},
            new(){ApplicationId=91,EventId=2,FromStage="Reviewing",ToStage="Interview",CreatedAt=date,Actor="HR",Note="new-marker"}];
        using var sp=Services(api);await using var r=new Live(sp);await r.Start(api.Detail());
        Assert.True(r.Text.IndexOf("new-marker",StringComparison.Ordinal)<r.Text.IndexOf("old-marker",StringComparison.Ordinal));
        Assert.Contains("12/11/2026 10:00",r.Text);
    }
}
#pragma warning restore BL0006
