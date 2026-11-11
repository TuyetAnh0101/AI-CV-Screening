extern alias web;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;
using web::SmartCv.Web.Components.Prompts;

namespace SmartCv.Tests;

public class EvaluationSetWorkspaceTests
{
    private sealed class Server : HttpMessageHandler
    {
        public List<EvaluationSetDto> Sets = [];
        public List<HrLabelDto> Labels = [];
        public int Posts, Puts; public bool FailList, FailSave, LoseCreate;
        public TaskCompletionSource? CreateGate;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Post)
            {
                Posts++; if (CreateGate is not null) await CreateGate.Task.WaitAsync(ct);
                var input = (await req.Content!.ReadFromJsonAsync<CreateEvaluationSetRequest>(ct))!;
                var saved = new EvaluationSetDto { EvaluationSetId = 10, Name = input.Name, Description = input.Description }; Sets.Add(saved);
                if (LoseCreate) throw new HttpRequestException("Fixture lost response");
                return Json(saved);
            }
            if (req.Method == HttpMethod.Put)
            {
                Puts++; if (FailSave) return Json(new { detail = "Fixture save failed" }, HttpStatusCode.ServiceUnavailable);
                var input = (await req.Content!.ReadFromJsonAsync<SaveHrLabelsRequest>(ct))!;
                Labels = input.Labels.Select(l => new HrLabelDto { ApplicationId = l.ApplicationId, IsPass = l.IsPass, Note = l.Note }).ToList();
                return Json(new SaveHrLabelsResult { LabelCount = Labels.Count, PassCount = Labels.Count(l => l.IsPass) });
            }
            if (path == "/api/evaluation-sets") return FailList ? Json(new { detail = "Fixture load failed" }, HttpStatusCode.ServiceUnavailable) : Json(Sets);
            if (path.EndsWith("applications")) return Json(new List<LabelCandidateDto> {
                new(){ ApplicationId=1, ExtractionStatus="Completed", HasExtractedText=true, CandidateName="HR phải tự đọc CV", FileName="fixture.pdf" },
                new(){ ApplicationId=2, ExtractionStatus="Completed", HasExtractedText=true },
                new(){ ApplicationId=3, ExtractionStatus="Failed", HasExtractedText=true },
                new(){ ApplicationId=4, ExtractionStatus="Completed", HasExtractedText=false } });
            return Json(new EvaluationSetDetailDto { EvaluationSetId = 10, Labels = Labels });
        }
        private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value) };
    }
    private static PromptApiClient Api(Server server) => new(new HttpClient(server) { BaseAddress = new Uri("https://fixture.test/") }, NullLogger<PromptApiClient>.Instance);
    private static async Task<EvaluationSetWorkspace> Draft(Server server)
    {
        var draft = new EvaluationSetWorkspace(Api(server)) { Name="Bộ HR", LabeledBy="HR fixture" };
        await draft.LoadAsync(default); await draft.CreateAsync(default); return draft;
    }
    [Fact]
    public async Task Empty_and_http_error_are_distinct_and_invalid_metadata_does_not_post()
    {
        var server=new Server();var draft=new EvaluationSetWorkspace(Api(server));
        await draft.LoadAsync(default);Assert.True(draft.Loaded);Assert.Empty(draft.Sets);Assert.Null(draft.Error);
        await draft.CreateAsync(default);Assert.NotNull(draft.Error);Assert.Equal(0,server.Posts);
        server.FailList=true;await draft.LoadAsync(default);Assert.False(draft.Loaded);Assert.Contains("load failed",draft.Error);
    }
    [Fact]
    public async Task No_implicit_hr_label_ineligible_rows_are_never_saved_and_notes_survive_failure()
    {
        var server=new Server();var draft=await Draft(server);
        Assert.All(draft.Rows,r=>Assert.Equal("",r.Choice));Assert.False(await draft.SaveAsync(default));Assert.Equal(0,server.Puts);
        draft.Rows[0].Choice="pass";draft.Rows[0].Note="Ghi chú HR gốc";draft.Rows[1].Choice="fail";
        draft.Rows[2].Choice="pass";draft.Rows[3].Choice="fail";server.FailSave=true;
        Assert.False(await draft.SaveAsync(default));Assert.Equal("Ghi chú HR gốc",draft.Rows[0].Note);Assert.Equal("pass",draft.Rows[0].Choice);
        server.FailSave=false;Assert.True(await draft.SaveAsync(default));Assert.Equal(2,server.Labels.Count);Assert.True(draft.Rows[0].HadLabel);
        var reload=new EvaluationSetWorkspace(Api(server));await reload.LoadAsync(default);await reload.SelectAsync(10,default);
        Assert.Equal("pass",reload.Rows[0].Choice);Assert.Equal("Ghi chú HR gốc",reload.Rows[0].Note);
    }
    [Fact]
    public async Task Double_click_creates_once_and_uncertain_post_is_not_retried()
    {
        var server=new Server {CreateGate=new()};var draft=new EvaluationSetWorkspace(Api(server)){Name="Bộ HR"};
        var first=draft.CreateAsync(default);await draft.CreateAsync(default);Assert.Equal(1,server.Posts);
        server.CreateGate.SetResult();await first;Assert.Equal(10,draft.SetId);
        var lost=new Server {LoseCreate=true};var uncertain=new EvaluationSetWorkspace(Api(lost)){Name="Bộ HR"};
        await uncertain.CreateAsync(default);Assert.True(uncertain.CreationUncertain);
        await uncertain.CreateAsync(default);Assert.Equal(1,lost.Posts);
        await uncertain.LoadAsync(default);Assert.False(uncertain.CreationUncertain);Assert.Equal(10,uncertain.SetId);Assert.Equal(1,lost.Posts);
    }
    [Fact]
    public async Task Empty_manager_renders_working_creation_form_and_explains_existing_uploads()
    {
        var server=new Server();using var services=new ServiceCollection().AddLogging().AddSingleton(Api(server)).BuildServiceProvider();
        await using var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());
        var html=await renderer.Dispatcher.InvokeAsync(async()=>WebUtility.HtmlDecode((await renderer.RenderComponentAsync<EvaluationSetManager>()).ToHtmlString()));
        Assert.Contains("Chưa có bộ CV đánh giá",html);Assert.Contains("Tên bộ CV mới",html);Assert.Contains("1. Lưu tên bộ",html);
        Assert.Contains("Không cần upload lại",html);Assert.Equal(0,server.Posts);
    }
}
