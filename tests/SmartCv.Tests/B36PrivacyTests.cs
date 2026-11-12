extern alias web;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Services;
using web::SmartCv.Web.Components.Results;
using web::SmartCv.Web.Components.Pages;
using web::SmartCv.Web.Components.Shared;
using web::SmartCv.Web.Helpers;

namespace SmartCv.Tests;

public class B36PrivacyTests
{
    // Legacy tests deliberately exercise normal view. New B36 tests exercise fail-closed view.
    internal static AnonymityState NormalView() { var state=new AnonymityState();state.SetAsync(false).GetAwaiter().GetResult();return state; }

    private sealed class StorageJs : IJSRuntime
    {
        public readonly Dictionary<string,string> Data = [];
        public bool FailRead, FailWrite;
        public TaskCompletionSource? ReadGate;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier,default,args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier,CancellationToken cancellationToken,object?[]? args)
        {
            var key=(string)args![0]!;
            if(identifier.EndsWith("getItem"))
            {
                if(FailRead)throw new JSException("Synthetic storage unavailable");
                Data.TryGetValue(key,out var value); if(ReadGate is not null)await ReadGate.Task;
                return value is null ? default! : (TValue)(object)value;
            }
            if(FailWrite)throw new JSException("Synthetic write unavailable");
            if(identifier.EndsWith("setItem"))Data[key]=(string)args[1]!;
            else if(identifier.EndsWith("removeItem"))Data.Remove(key);
            return default!;
        }
    }

    [Fact]
    public async Task Protected_local_preference_survives_new_scopes_and_does_not_flash_identity()
    {
        var js=new StorageJs();var protection=new EphemeralDataProtectionProvider();
        var storage=new ProtectedLocalStorage(js,protection);var initial=new AnonymityState(storage);
        Assert.True(initial.Enabled);Assert.False(initial.Loaded);
        await initial.EnsureLoadedAsync();Assert.True(initial.Loaded);Assert.False(initial.Enabled);
        await initial.SetAsync(true);Assert.DoesNotContain("true",Assert.Single(js.Data).Value);
        var reopened=new AnonymityState(new ProtectedLocalStorage(js,protection));Assert.True(reopened.Enabled);
        await reopened.EnsureLoadedAsync();Assert.True(reopened.Enabled);
        await reopened.SetAsync(false);var reload=new AnonymityState(new ProtectedLocalStorage(js,protection));
        Assert.True(reload.Enabled);await reload.EnsureLoadedAsync();Assert.False(reload.Enabled);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Storage_failure_or_invalid_ciphertext_keeps_information_hidden(bool corrupted)
    {
        var js=new StorageJs { FailRead=!corrupted };if(corrupted)js.Data["scapp.anonymous"]="not-valid-protected-data";
        var state=new AnonymityState(new ProtectedLocalStorage(js,new EphemeralDataProtectionProvider()));
        await state.EnsureLoadedAsync();Assert.True(state.Enabled);Assert.False(state.Loaded);Assert.NotNull(state.Warning);
        js.FailRead=false;js.Data.Clear();await state.RetryAsync();Assert.False(state.Enabled);Assert.True(state.Loaded);
        js.FailWrite=true;await state.SetAsync(true);Assert.True(state.Enabled);Assert.NotNull(state.Warning);
    }

    [Fact]
    public async Task Late_storage_read_cannot_override_explicit_privacy_choice()
    {
        var js=new StorageJs();var storage=new ProtectedLocalStorage(js,new EphemeralDataProtectionProvider());
        await storage.SetAsync("scapp.anonymous",false);js.ReadGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var state=new AnonymityState(storage);var reading=state.EnsureLoadedAsync();Assert.True(state.Enabled);
        await state.SetAsync(true);js.ReadGate.SetResult();await reading;Assert.True(state.Enabled);
    }

    private static List<RankingItemDto> Rows() => Enumerable.Range(1,3).Select(i=>new RankingItemDto
    {
        EvaluationId=i,ApplicationId=90+i,Rank=i==3?2:1,CandidateName="Secret Nguyen "+i,FileName="PrivateCV"+i+".pdf",
        Status="Completed",FinalScore=i==3?60:85,Stage=i==3?"Rejected":"New",PredictedPass=i!=3,
        ReviewFlags=i==1?[new(){Code="ScoreGap",Message="Điểm chênh lệch"},new(){Code="ShortText",Message="Văn bản ngắn"}]:i==3?[new(){Code="KeywordOnly",Message="Chỉ từ khóa"}]:[]
    }).ToList();

    [Fact]
    public async Task Anonymous_search_aliases_and_tie_order_never_use_real_identity()
    {
        var state=new AnonymityState();var rows=Rows();
        Assert.NotEqual(state.Name(rows[0].CandidateName,1,91),state.Name(rows[1].CandidateName,1,92));
        Assert.All(rows,r=>{Assert.False(state.Matches(r,"Secret"));Assert.False(state.Matches(r,"PrivateCV"));});
        Assert.True(state.Matches(rows[0],"#1"));Assert.False(state.Matches(rows[2],"#1"));
        Assert.Equal(new[]{1,2,3},state.Sort(rows,RankingSort.NameAsc).Select(r=>r.EvaluationId));
        Assert.Equal("PrivateCV1.pdf",rows[0].FileName);await state.SetAsync(false);Assert.True(state.Matches(rows[0],"Secret"));
    }

    [Fact]
    public void Flag_filter_combines_fit_stage_search_and_sort_using_supplied_flags_only()
    {
        var table=new RankingTable();
        ParameterView.FromDictionary(new Dictionary<string,object?> { ["Items"]=Rows(),["FlaggedCount"]=2 }).SetParameterProperties(table);
        typeof(RankingTable).GetProperty("Anonymity",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(table,new AnonymityState());
        void Set(string key,object value)=>typeof(RankingTable).GetField(key,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(table,value);
        List<RankingItemDto> Visible()=>(List<RankingItemDto>)typeof(RankingTable).GetProperty("VisibleRows",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(table)!;
        Set("flaggedOnly",true);Assert.Equal(new[]{1,3},Visible().Select(r=>r.EvaluationId));
        Set("filter",RankingFilter.Pass);Assert.Equal(1,Assert.Single(Visible()).EvaluationId);
        Set("stageFilter","Rejected");Assert.Empty(Visible());Set("filter",RankingFilter.All);
        Assert.Equal(3,Assert.Single(Visible()).EvaluationId);Set("search","Secret");Assert.Empty(Visible());
        Set("search","#2");Assert.Equal(3,Assert.Single(Visible()).EvaluationId);
    }

    [Theory]
    [InlineData(409)] [InlineData(400)] [InlineData(404)] [InlineData(500)] [InlineData(null)]
    public void Anonymous_decision_errors_keep_useful_status_without_echoing_sensitive_API_text(int? status)
    {
        var table=new RankingTable();
        typeof(RankingTable).GetProperty("Anonymity",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(table,new AnonymityState());
        typeof(RankingTable).GetMethod("ShowFailure",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(table,["Secret Nguyen private-address",status]);
        var message=(string)typeof(RankingTable).GetField("message",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(table)!;
        Assert.DoesNotContain("Secret",message);Assert.DoesNotContain("private-address",message);
        if(status==409)Assert.Contains("HR khác",message);
        else if(status is null)Assert.Contains("không tự gửi lại",message);
        else Assert.False(string.IsNullOrWhiteSpace(message));
    }

    private sealed class Api : HttpMessageHandler
    {
        public int Writes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(request.Method!=HttpMethod.Get){Writes++;throw new InvalidOperationException("Privacy must not write HR decisions");}
            object data=request.RequestUri!.AbsolutePath.EndsWith("events")
                ? new List<ApplicationEventDto>{new(){ApplicationId=91,EventId=1,FromStage="New",ToStage="Reviewing",Note="Secret Nguyen lives at Private Address",Actor="Secret Nguyen",CreatedAt=DateTime.UtcNow}}
                : new EvaluationDetailDto {EvaluationId=22,ApplicationId=91,Rank=1,StageVersion=Guid.NewGuid(),CandidateName="Secret Nguyen",CandidateEmail="private@example.test",FileName="PrivateCV.pdf",
                    Status="Completed",ScoreSource="Combined",KeywordScore=40,AiScore=90,FinalScore=69,FitLabel="Secret Nguyen",Reasoning="Secret Nguyen lives at Private Address",AiRawResponse="{\"name\":\"Secret Nguyen\"}",
                    Skills=[new(){DisplayName="C#",SkillType="MustHave",IsMatched=true,Evidence="Private Address - Secret Nguyen"}],ReviewFlags=[new(){Code="ScoreGap",Message="Điểm chênh lệch"}]};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(data,data.GetType())});
        }
    }
    private sealed class Nav : NavigationManager { public Nav(){Initialize("https://web.test/","https://web.test/results/evaluations/22");} protected override void NavigateToCore(string uri,bool forceLoad){} }
    private sealed class BoundaryLogger : IErrorBoundaryLogger { public ValueTask LogErrorAsync(Exception exception) => throw exception; }
    private static ServiceProvider Services(AnonymityState state,Api api)
    {
        var http=new HttpClient(api){BaseAddress=new Uri("https://api.test/")};
        return new ServiceCollection().AddLogging().AddSingleton(state).AddSingleton(new HrActorState())
            .AddSingleton<IErrorBoundaryLogger,BoundaryLogger>()
            .AddSingleton<NavigationManager>(new Nav()).AddSupplyValueFromQueryProvider()
            .AddSingleton(new ApplicationApiClient(http,NullLogger<ApplicationApiClient>.Instance))
            .AddSingleton(new EvaluationApiClient(http,NullLogger<EvaluationApiClient>.Instance)).BuildServiceProvider();
    }
    private static async Task<string> Html<T>(ServiceProvider services,Dictionary<string,object?> parameters) where T:IComponent
    {
        await using var renderer=new HtmlRenderer(services,NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async()=>System.Net.WebUtility.HtmlDecode((await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString()));
    }
    [Fact]
    public async Task Prerender_masks_all_candidate_surfaces_notes_and_raw_JSON_without_any_write()
    {
        var api=new Api();using var services=Services(new AnonymityState(),api);
        var ranking=await Html<RankingTable>(services,new(){["Items"]=Rows(),["FlaggedCount"]=2});
        var detail=await Html<EvaluationDetail>(services,new(){["Id"]=22});
        foreach(var html in new[]{ranking,detail})foreach(var secret in new[]{"Secret Nguyen","private@example.test","PrivateCV","Private Address"})Assert.DoesNotContain(secret,html);
        Assert.Contains("Ứng viên #1",ranking);Assert.DoesNotContain("Tên ứng viên: A",ranking);
        Assert.Contains("Tắt Ẩn danh để xem CV gốc",detail);Assert.DoesNotContain("<pre",detail);Assert.DoesNotContain("target=\"_blank\"",detail);
        Assert.Contains("Cần xem kỹ trước khi quyết định",detail);Assert.Contains("Ghi chú được ẩn",detail);Assert.Equal(0,api.Writes);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)]
    public async Task Flag_badge_renders_all_API_reasons_and_accessible_control(int count)
    {
        using var services=Services(new AnonymityState(),new Api());
        var html=await Html<ReviewFlagsBadge>(services,new(){["Flags"]=Enumerable.Range(1,count).Select(i=>new ReviewFlagDto{Code="Code"+i,Message="Reason "+i}).ToList()});
        if(count==0)Assert.DoesNotContain("<button",html);
        else { Assert.Contains("aria-describedby",html);for(var i=1;i<=count;i++)Assert.Contains("Reason "+i,html); }
    }

    [Fact]
    public void Keyboard_focus_keeps_reasons_open_when_pointer_leaves_but_blur_and_Escape_close()
    {
        var badge=new ReviewFlagsBadge();
        void Event(string name,params object?[] args)=>typeof(ReviewFlagsBadge).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(badge,args);
        bool Open()=>(bool)typeof(ReviewFlagsBadge).GetField("open",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(badge)!;
        Event("Focus");Event("Leave");Assert.True(Open());Event("Blur");Assert.False(Open());
        Event("Pin");Event("Leave");Assert.True(Open());Event("Key",new KeyboardEventArgs{Key="Escape"});Assert.False(Open());
    }
}
