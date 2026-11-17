extern alias web;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Contracts;
using SmartCv.Core.Privacy;
using web::SmartCv.Web.Components.Prompts;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

#pragma warning disable BL0006 // Real Blazor event dispatch without an extra testing package.
public class B41PromptMetricsTests
{
    [Theory]
    [InlineData(70, 40, 89, "70% (40–89%)")]
    [InlineData(0, 0, 28, "0% (0–28%)")]
    [InlineData(100, 72, 100, "100% (72–100%)")]
    [InlineData(70, 90, 40, "70%")]
    [InlineData(70, -1, 89, "70%")]
    [InlineData(70, 40, 101, "70%")]
    public void Wilson_format_preserves_zero_and_rejects_invalid_intervals(int percent, int low, int high, string expected) =>
        Assert.Equal(expected, AccuracyText.WithRange(percent, low, high));

    [Fact]
    public void Missing_values_are_not_zero_and_Vietnamese_decimal_format_is_stable()
    {
        Assert.Equal("—", AccuracyText.WithRange(null, 0, 0));
        Assert.Equal("70%", AccuracyText.WithRange(70, null, null));
        Assert.Equal("66,67% (39–87%)", AccuracyText.WithRange(66.67m, 39.06m, 86.62m));
        Assert.Equal("", AccuracyText.Range(null, 80));
        Assert.True(AccuracyText.Overlap(40, 80, 80, 90));
        Assert.False(AccuracyText.Overlap(null, 80, 70, 90));
        Assert.Equal(40m, AccuracyText.Width(40, 80));
        Assert.Equal("66.67", AccuracyText.Css(66.67m));
    }

    private static PromptTestRunDto Run(int id, int version, int correct = 7, int set = 1, decimal low = 40, decimal high = 89) => new()
    {
        RunId = id, PromptVersionId = version, PromptVersionName = $"v{version}", EvaluationSetId = set,
        Status = "Completed", TotalItems = 10, TotalLabeledCount = 10, CorrectCount = correct,
        MissedCount = 1, WrongPickCount = 8 - correct, UnscoredCount = 1, KeywordOnlyCount = 0,
        AccuracyPercent = correct * 10, AccuracyLow = low, AccuracyHigh = high, ModelName = "fixture",
        CompletedAt = new DateTime(2026, 11, 17, 1, 0, 0, DateTimeKind.Utc),
        Matrix = new() { TruePass = correct - 2, TrueFail = 2, Missed = 1, WrongPick = 8 - correct, Unscored = 1 }
    };
    private static PromptTestSummaryDto Summary(PromptTestRunDto r, decimal? low = null, decimal? high = null) => new()
    {
        RunId = r.RunId, PromptVersionId = r.PromptVersionId, EvaluationSetId = r.EvaluationSetId,
        AccuracyPercent = r.AccuracyPercent, AccuracyLow = low ?? r.AccuracyLow, AccuracyHigh = high ?? r.AccuracyHigh,
        CorrectCount = r.CorrectCount!.Value, MissedCount = r.MissedCount!.Value, WrongPickCount = r.WrongPickCount!.Value
    };
    [Theory]
    [InlineData(false, true, "Better", ConclusionKind.Inconclusive)]
    [InlineData(false, false, "NotComparable", ConclusionKind.NotComparable)]
    [InlineData(true, true, "Better", ConclusionKind.Stale)]
    [InlineData(true, true, "Same", ConclusionKind.NotComparable)]
    public void Conclusion_never_infers_superiority_from_observed_accuracy(bool conclusive, bool fair, string verdict, ConclusionKind kind)
    {
        var a = Run(11, 1); var b = Run(22, 2, 8);
        var result = ComparisonConclusion.Read(new() { Base = Summary(a), Candidate = Summary(b),
            Conclusive = conclusive, IsFairComparison = fair, Verdict = verdict, Summary = "Bản thử có tỷ lệ quan sát cao hơn." }, a, b, 1);
        Assert.Equal(kind, result.Kind);
        Assert.False(result.IsConclusive);
        if (kind == ConclusionKind.Inconclusive) Assert.Equal(AccuracyText.Inconclusive, result.Title);
    }
    [Fact]
    public void Conclusion_uses_API_summary_only_with_consistent_disjoint_intervals()
    {
        var a = Run(11, 1, 2, low: 5, high: 35); var b = Run(22, 2, 8, low: 60, high: 98);
        var result = ComparisonConclusion.Read(new() { Base = Summary(a), Candidate = Summary(b),
            Conclusive = true, IsFairComparison = true, Verdict = "Better", Summary = "Kết luận từ API trên bộ tổng hợp." }, a, b, 1);
        Assert.True(result.IsConclusive); Assert.Equal("Kết luận từ API trên bộ tổng hợp.", result.Message);
        Assert.Equal(ConclusionKind.Stale, ComparisonConclusion.Read(new() { Base = Summary(a), Candidate = Summary(Run(33, 2)) }, a, b, 1).Kind);
        Assert.Equal(ConclusionKind.NotComparable, ComparisonConclusion.Read(new() { Base = Summary(a), Candidate = Summary(b) }, a, b, 2).Kind);
        Assert.Equal(ConclusionKind.NeedTwoRuns, ComparisonConclusion.Pair([a], a, 1, 1).Reason!.Kind);
        Assert.Equal(ConclusionKind.SameRun, ComparisonConclusion.Pair([a,b], a, 1, 1).Reason!.Kind);
        Assert.Equal(ConclusionKind.NoActiveBase, ComparisonConclusion.Pair([a,b], b, null, 1).Reason!.Kind);
        Assert.Equal(ConclusionKind.Error, ComparisonConclusion.Failed("409").Kind);
    }

    private static LabelSuggestionDto Suggestion(int id, string status = "New", bool suggested = true) => new()
    {
        ApplicationId = id, CandidateName = "Tên tổng hợp riêng tư", FileName = "cv-tong-hop.pdf", JobId = 9, JobTitle = "Backend Developer",
        Stage = suggested ? "Interview" : "Rejected", RejectReason = suggested ? null : "MissingSkills",
        SuggestedIsPass = suggested, CurrentLabel = status == "New" ? null : status == "Same" ? suggested : !suggested,
        Status = status, Reason = "Lý do từ HR", StageUpdatedAt = new DateTime(2026,11,17)
    };
    private static LabelSuggestionResponse Suggestions(int set = 1, bool afterSave = false) => new()
    {
        EvaluationSetId = set, NewCount = afterSave ? 0 : 1, SameCount = afterSave ? 2 : 1, ConflictCount = 1, SkippedCount = 3,
        Items = [Suggestion(11, afterSave ? "Same" : "New"), Suggestion(12, "Conflict", false), Suggestion(13, "Same")]
    };
    [Fact]
    public void Selection_defaults_conflicts_same_bulk_and_payload_are_explicit()
    {
        var draft = new LabelSuggestionDraft(); draft.Load(Suggestions());
        Assert.Equal(new[] {11}, draft.PickedItems.Select(s => s.ApplicationId));
        Assert.False(draft.IsPicked(Suggestion(12,"Conflict",false)));
        Assert.False(draft.Selectable(Suggestion(13,"Same")));
        draft.ToggleAllNew(true); Assert.Equal(1,draft.PickedCount);
        draft.Toggle(Suggestion(12,"Conflict",false),true);
        var request = draft.BuildRequest("  HR Tổng hợp  ").Request!;
        Assert.Equal("HR Tổng hợp",request.LabeledBy);
        Assert.Equal(new[]{11,12},request.Labels.Select(l=>l.ApplicationId));
        Assert.All(request.Labels,l=>Assert.StartsWith("Từ quyết định: ",l.Note));
        Assert.False(request.Labels[1].IsPass);
        Assert.Null(draft.BuildRequest(" ").Request);
        Assert.Null(draft.BuildRequest(new string('x',101)).Request);
        draft.Load(Suggestions()); Assert.True(draft.IsPicked(Suggestion(12,"Conflict",false)));
        draft.Load(Suggestions(afterSave:true)); Assert.False(draft.IsPicked(Suggestion(11,"Same")));
    }
    [Fact]
    public void Duplicate_anonymized_and_non_decision_rows_cannot_be_written()
    {
        var draft = new LabelSuggestionDraft(); draft.Load(new() { Items = [Suggestion(11),Suggestion(11)] });
        Assert.Equal(1,draft.AmbiguousCount); Assert.Equal(0,draft.PickedCount);
        Assert.False(LabelSuggestionSelection.CanCheck(new() { ApplicationId = 1, Status = "New", Stage = "OnHold", SuggestedIsPass=true }));
        Assert.False(LabelSuggestionSelection.CanCheck(new() { ApplicationId = 1, Status = "New", Stage = "Interview", SuggestedIsPass=true, FileName=RetentionRules.AnonymizedFileName }));
        Assert.Null(LabelSuggestionSelection.BuildRequest([Suggestion(11),Suggestion(11)],"HR").Request);
        Assert.True(LabelSuggestionSelection.NoteFor(Suggestion(11)).Length<=500);
    }

    [Fact]
    public void Notes_are_bounded_and_old_conflict_events_cannot_select_a_changed_decision()
    {
        Assert.Equal(500,LabelSuggestionSelection.NoteFor(new(){Reason=new string('x',1000)}).Length);
        var old=Suggestion(12,"Conflict",false);var draft=new LabelSuggestionDraft();
        draft.Load(new(){Items=[old]});
        var changed=new LabelSuggestionDto { ApplicationId=12,Status="Conflict",Stage="Rejected",RejectReason="OtherRequirement",
            SuggestedIsPass=false,CurrentLabel=true,StageUpdatedAt=new DateTime(2026,11,18) };
        draft.Load(new(){Items=[changed]});draft.Toggle(old,true);
        Assert.Equal(0,draft.PickedCount);draft.Toggle(changed,true);Assert.Equal(1,draft.PickedCount);
    }

    [Fact]
    public void A_conclusive_response_without_valid_intervals_is_not_presented_as_a_conclusion()
    {
        var a=Run(11,1);var b=Run(22,2,8);
        var result=ComparisonConclusion.Read(new(){Base=Summary(a,low:-1),Candidate=Summary(b),IsFairComparison=true,Conclusive=true,Verdict="Better"},a,b,1);
        Assert.Equal(ConclusionKind.MissingInterval,result.Kind);Assert.False(result.IsConclusive);
    }

    private sealed class Server : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public List<SaveHrLabelsRequest> Bodies { get; } = [];
        public int? Failure;
        public bool Timeout, Saved, FailReads, WrongSet, NetworkFailure, Unavailable;
        public TaskCompletionSource? PutGate, GetGate;
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondSetRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<PromptTestRunDto> Runs { get; set; } = [Run(11,1),Run(22,2,8)];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path=request.RequestUri!.PathAndQuery; Calls.Add($"{request.Method} {path}");
            if(request.Method==HttpMethod.Put)
            {
                Bodies.Add((await request.Content!.ReadFromJsonAsync<SaveHrLabelsRequest>(ct))!);
                if(PutGate is not null) await PutGate.Task.WaitAsync(ct);
                if(NetworkFailure) throw new HttpRequestException("Synthetic disconnected transport");
                if(Timeout) { Saved=true; throw new TaskCanceledException("Synthetic timeout after commit"); }
                if(Failure is {} failure) return Json(new {detail="Lỗi tổng hợp"},failure);
                Saved=true; return Json(new SaveHrLabelsResult { Added=1,Updated=Bodies.Last().Labels.Count-1,LabelCount=3 });
            }
            if(FailReads) return Json(new{detail="Không đọc được trạng thái"},500);
            if(path.Contains("label-suggestions")) return Json(Suggestions(WrongSet?99:1,Saved));
            if(path=="/api/evaluation-sets/1") return Json(new EvaluationSetDetailDto { EvaluationSetId=1,
                Labels=Saved?Bodies.SelectMany(b=>b.Labels.Select(l=>new HrLabelDto { ApplicationId=l.ApplicationId,IsPass=l.IsPass,Note=l.Note,LabeledBy=b.LabeledBy })).ToList():[] });
            if(path.EndsWith("/applications")) return Json(new[]{11,12,13}.Select(id=>new LabelCandidateDto{ApplicationId=id,FileName=Unavailable?RetentionRules.AnonymizedFileName:"fixture.pdf",JobTitle="Backend",HasExtractedText=!Unavailable,ExtractionStatus="Completed"}).ToList());
            if(path=="/api/evaluation-sets") return Json(new[]{new EvaluationSetDto{EvaluationSetId=1,Name="Bộ tổng hợp",LabelCount=10,PassCount=5},new EvaluationSetDto{EvaluationSetId=2,Name="Bộ 2",LabelCount=10,PassCount=5}});
            if(path.Contains("/compare")) return Json(new PromptCompareDto {Base=Summary(Runs[0]),Candidate=Summary(Runs[1]),IsFairComparison=true,Verdict="Better",Conclusive=false});
            if(path.StartsWith("/api/prompt-tests?"))
            {
                var set=path.EndsWith("=2")?2:1;
                if(set==1 && GetGate is not null) { ReadStarted.TrySetResult(); await GetGate.Task; }
                if(set==2) SecondSetRead.TrySetResult();
                return Json(Runs.Where(r=>r.EvaluationSetId==set).ToList());
            }
            return Json(new{detail="Không có route fixture"},404);
        }
        private static HttpResponseMessage Json(object value,int status=200)=>new((HttpStatusCode)status){Content=JsonContent.Create(value)};
    }
    private static PromptApiClient Api(Server s)=>new(new HttpClient(s){BaseAddress=new Uri("http://fixture.test/")},NullLogger<PromptApiClient>.Instance);

    [Fact]
    public async Task GET_is_read_only_and_PUT_contains_only_chosen_labels()
    {
        var server=new Server(); using var state=new LabelSuggestionWorkspace(Api(server),1);
        await state.LoadAsync(); Assert.Empty(server.Bodies); Assert.Equal(1,state.Draft.PickedCount);
        state.Actor=" HR "; state.Draft.Toggle(Suggestion(11),false); state.Draft.Toggle(Suggestion(12,"Conflict",false),true);
        Assert.True(await state.SaveAsync());
        var body=Assert.Single(server.Bodies); Assert.Equal("HR",body.LabeledBy); Assert.Equal(12,Assert.Single(body.Labels).ApplicationId);
        Assert.Contains("thêm 1",state.Message); Assert.NotNull(state.Saved);
    }
    [Fact]
    public async Task Actor_validation_and_double_submit_do_not_write_extra_requests()
    {
        var server=new Server{PutGate=new()}; using var state=new LabelSuggestionWorkspace(Api(server),1);
        await state.LoadAsync(); Assert.False(await state.SaveAsync()); Assert.Empty(server.Bodies);
        state.Actor="HR";var first=state.SaveAsync();Assert.True(state.Saving);
        Assert.False(await state.SaveAsync());Assert.Single(server.Bodies);
        server.PutGate.SetResult();Assert.True(await first);Assert.Single(server.Bodies);
    }
    [Theory]
    [InlineData(400,false)] [InlineData(409,true)] [InlineData(429,false)] [InlineData(500,true)]
    public async Task HTTP_errors_keep_selection_or_require_fresh_review(int code,bool readBack)
    {
        var server=new Server{Failure=code};using var state=new LabelSuggestionWorkspace(Api(server),1){Actor="HR"};
        await state.LoadAsync();await state.SaveAsync();Assert.NotNull(state.Error);Assert.Null(state.Saved);
        Assert.Equal(readBack,server.Calls.Contains("GET /api/evaluation-sets/1"));
        Assert.Equal(readBack?0:1,state.Draft.PickedCount);Assert.Single(server.Bodies);
    }
    [Fact]
    public async Task Timeout_after_commit_reads_labels_before_retry_and_never_sends_automatic_PUT()
    {
        var server=new Server{Timeout=true};using var state=new LabelSuggestionWorkspace(Api(server),1){Actor="HR"};
        await state.LoadAsync();Assert.True(await state.SaveAsync());Assert.Single(server.Bodies);
        Assert.Contains("Máy chủ hiện có đủ",state.Message);Assert.False(state.NeedsVerification);
        Assert.Contains("GET /api/evaluation-sets/1",server.Calls);Assert.False(await state.SaveAsync());Assert.Single(server.Bodies);
    }
    [Fact]
    public async Task Failed_reconciliation_locks_writes_until_server_state_is_read()
    {
        var server=new Server{PutGate=new(),Timeout=true};using var state=new LabelSuggestionWorkspace(Api(server),1){Actor="HR"};
        await state.LoadAsync();var save=state.SaveAsync();server.FailReads=true;server.PutGate.SetResult();
        Assert.False(await save);Assert.True(state.NeedsVerification);Assert.False(state.CanSave);
        Assert.False(await state.SaveAsync());Assert.Single(server.Bodies);
        server.FailReads=false;Assert.True(await state.LoadAsync());Assert.False(state.NeedsVerification);Assert.Single(server.Bodies);
    }
    [Fact]
    public async Task Wrong_set_response_is_never_loaded()
    {
        var server=new Server{WrongSet=true};using var state=new LabelSuggestionWorkspace(Api(server),1);
        Assert.False(await state.LoadAsync());Assert.Null(state.Draft.Data);Assert.False(state.CanSave);
    }

    [Fact]
    public async Task Disconnected_transport_is_reconciled_and_requires_another_deliberate_selection()
    {
        var server=new Server{NetworkFailure=true};using var state=new LabelSuggestionWorkspace(Api(server),1){Actor="HR"};
        await state.LoadAsync();await state.SaveAsync();Assert.Contains("GET /api/evaluation-sets/1",server.Calls);
        Assert.NotNull(state.Error);Assert.Null(state.Saved);Assert.Equal(0,state.Draft.PickedCount);Assert.Single(server.Bodies);
    }

    [Fact]
    public async Task Suggestions_support_the_real_optional_job_filter_without_writes()
    {
        var server=new Server();await Api(server).GetLabelSuggestionsAsync(1,jobId:9);
        Assert.Equal("GET /api/evaluation-sets/1/label-suggestions?jobId=9",Assert.Single(server.Calls));Assert.Empty(server.Bodies);
    }

    [Fact]
    public async Task Anonymized_or_unavailable_profiles_are_locked_and_rechecked_before_PUT()
    {
        var server=new Server();using var state=new LabelSuggestionWorkspace(Api(server),1){Actor="HR"};
        await state.LoadAsync();Assert.Equal(1,state.Draft.PickedCount);
        server.Unavailable=true;Assert.False(await state.SaveAsync());Assert.Empty(server.Bodies);Assert.False(state.Fresh);
        await state.LoadAsync();Assert.Equal(0,state.Draft.PickedCount);Assert.False(state.Draft.Selectable(Suggestion(11)));
    }

    private static ServiceProvider Services(Server server, AnonymityState? privacy=null)=>new ServiceCollection().AddLogging().AddSingleton(Api(server))
        .AddSingleton(privacy??new AnonymityState()).AddSingleton(new HrActorState()).BuildServiceProvider();
    private static async Task<string> Html<T>(ServiceProvider services,Dictionary<string,object?>? parameters=null) where T:IComponent
    {
        await using var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async()=>WebUtility.HtmlDecode((await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters??[]))).ToHtmlString()));
    }
    [Fact]
    public async Task Matrix_orientation_is_HR_rows_AI_columns_and_unscored_is_outside_four_cells()
    {
        using var services=Services(new());var run=Run(11,1);
        Assert.Null(AccuracyText.MatrixIssue(run));
        var html=await Html<ConfusionMatrix>(services,new(){["Matrix"]=run.Matrix,["Denominator"]=10});
        var table=html[html.IndexOf("<tbody",StringComparison.Ordinal)..html.IndexOf("</tbody>",StringComparison.Ordinal)];
        Assert.Equal(4,System.Text.RegularExpressions.Regex.Matches(table,"<td ").Count);
        Assert.True(table.IndexOf("HR quyết định",StringComparison.Ordinal)<table.IndexOf("cell ok",StringComparison.Ordinal));
        Assert.True(table.IndexOf("cell missed",StringComparison.Ordinal)<table.IndexOf("cell wrong",StringComparison.Ordinal));
        Assert.Matches("class=\"cell-value\"[^>]*>5</span>",table);Assert.Contains("Bỏ sót",table);Assert.Contains("AI dự đoán",html);
        Assert.Contains("+ 1 CV chưa có dự đoán hợp lệ",html);Assert.Contains("mẫu số: 10",html);
        Assert.DoesNotContain("<table",await Html<ConfusionMatrix>(services));
    }
    [Fact]
    public async Task Panel_is_anonymous_and_loading_does_not_save()
    {
        var server=new Server();using var services=Services(server);using var state=new LabelSuggestionWorkspace(Api(server),1);
        await state.LoadAsync();var html=await Html<LabelSuggestionPanel>(services,new(){["State"]=state});
        Assert.Contains("Lưu 1 nhãn được chọn",html);Assert.Contains("Conflict",html);Assert.Contains("Same",html);
        Assert.DoesNotContain("Tên tổng hợp riêng tư",html);Assert.DoesNotContain("cv-tong-hop.pdf",html);
        Assert.DoesNotContain("CurrentNote",html);Assert.Empty(server.Bodies);
        var same=html[html.IndexOf("aria-label=\"Chọn hồ sơ #13\"",StringComparison.Ordinal)..];
        Assert.StartsWith("aria-label=\"Chọn hồ sơ #13\" disabled",same);
    }
    [Fact]
    public async Task Manual_draft_survives_label_refresh_while_untouched_rows_receive_server_labels()
    {
        var server=new Server();var api=Api(server);var workspace=new EvaluationSetWorkspace(api);
        await workspace.LoadAsync(default);await workspace.SelectAsync(1,default);
        workspace.Rows[0].Choice="fail";workspace.Rows[0].Note="Bản nháp thủ công";
        await workspace.RefreshLabelsPreservingDraftAsync(default);
        Assert.Equal("fail",workspace.Rows[0].Choice);Assert.Equal("Bản nháp thủ công",workspace.Rows[0].Note);
        Assert.True(workspace.Rows[0].IsDirty);
    }

    private sealed class EventRenderer(IServiceProvider services):Renderer(services,NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; }=Dispatcher.CreateDefault();
        private int root=-1;
        protected override Task UpdateDisplayAsync(in RenderBatch batch)=>Task.CompletedTask;
        protected override void HandleException(Exception exception)=>System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);
        public Task Show<T>(Dictionary<string,object?> parameters) where T:IComponent=>Dispatcher.InvokeAsync(async()=>
        { if(root<0)root=AssignRootComponentId(InstantiateComponent(typeof(T)));await RenderRootComponentAsync(root,ParameterView.FromDictionary(parameters)); });
        public Task Change(string label,object value)=>Dispatcher.InvokeAsync(async()=>
        {
            var frames=GetCurrentRenderTreeFrames(root);
            for(var i=0;i<frames.Count;i++)
            {
                if(frames.Array[i].FrameType!=RenderTreeFrameType.Element)continue;
                var attrs=frames.Array.Skip(i+1).TakeWhile(f=>f.FrameType==RenderTreeFrameType.Attribute).ToList();
                if(!attrs.Any(f=>f.AttributeName=="aria-label" && f.AttributeValue?.ToString()==label))continue;
                var evt=attrs.Single(f=>f.AttributeName=="onchange");
                await DispatchEventAsync(evt.AttributeEventHandlerId,null,new ChangeEventArgs{Value=value});return;
            }
            throw new InvalidOperationException($"Missing {label}");
        });
        public Task<string> Text()=>Dispatcher.InvokeAsync(()=>string.Join(" ",GetCurrentRenderTreeFrames(root).Array.Take(GetCurrentRenderTreeFrames(root).Count)
            .Where(f=>f.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup).Select(f=>f.FrameType==RenderTreeFrameType.Text?f.TextContent:f.MarkupContent)));
    }
    [Fact]
    public async Task Actual_checkbox_events_change_only_explicit_selection()
    {
        var server=new Server();using var services=Services(server);using var state=new LabelSuggestionWorkspace(Api(server),1){Actor="HR"};
        await state.LoadAsync();await using var renderer=new EventRenderer(services);
        await renderer.Show<LabelSuggestionPanel>(new(){["State"]=state});Assert.Empty(server.Bodies);
        await renderer.Change("Chọn hồ sơ #11",false);await renderer.Change("Chọn hồ sơ #12",true);
        Assert.Equal(12,Assert.Single(state.Draft.PickedItems).ApplicationId);Assert.Empty(server.Bodies);
        await state.SaveAsync();Assert.Equal(12,Assert.Single(Assert.Single(server.Bodies).Labels).ApplicationId);
    }
    [Fact]
    public async Task Switching_sets_ignores_late_response_even_if_handler_does_not_honor_cancellation()
    {
        var server=new Server{GetGate=new()};server.Runs.Add(Run(33,3,set:2));using var services=Services(server);
        await using var renderer=new EventRenderer(services);
        var first=renderer.Show<AccuracyCompare>(new(){["EvaluationSetId"]=1});await server.ReadStarted.Task;
        var second=renderer.Show<AccuracyCompare>(new(){["EvaluationSetId"]=2});
        await server.SecondSetRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        server.GetGate.SetResult();await Task.WhenAll(first,second);var text=await renderer.Text();
        Assert.Contains("v3",text);Assert.DoesNotContain("v1",text);Assert.DoesNotContain("v2",text);
    }
}
#pragma warning restore BL0006
