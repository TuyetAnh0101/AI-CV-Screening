using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

/// <summary>A9: GET /api/applications/{id}/keyword-score qua pipeline HTTP (database InMemory có 5 job nền).</summary>
public class KeywordScoreEndpointTests
{
    private const int BackendJob = 1;
    private const int AiJob = 3;

    private sealed record Env(WebApplicationFactory<Program> Factory, HttpClient Client) : IDisposable
    {
        public void Dispose() { Client.Dispose(); Factory.Dispose(); }

        public async Task<T> DbAsync<T>(Func<SmartCvDbContext, Task<T>> action)
        {
            using var scope = Factory.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<SmartCvDbContext>());
        }
    }

    private static async Task<Env> CreateAsync(string environment = "Development")
    {
        var factory = TestSupport.ApiFactory(environment, dbName: Guid.NewGuid().ToString("N"));
        var env = new Env(factory, factory.CreateClient());
        await env.DbAsync(db => db.Database.EnsureCreatedAsync());
        return env;
    }

    private static async Task<int> AddApplicationAsync(Env env, int jobId, string status, string? text, string method = ExtractionMethods.PdfText)
    {
        var id = Guid.NewGuid();
        return await env.DbAsync(async db =>
        {
            db.Cvs.Add(new Cv
            {
                CvId = id, OriginalFileName = "CV_Demo.pdf", StoredPath = $"Uploads/CV/2026/11/{id}.pdf", ContentType = "application/pdf",
                FileSizeBytes = 1, FileHash = id.ToString("N").PadRight(64, '0').ToUpperInvariant(), ExtractionStatus = status,
                ExtractionMethod = method, ExtractedText = text, UploadedAt = DateTime.UtcNow
            });
            var application = new Application { CvId = id, JobId = jobId, CreatedAt = DateTime.UtcNow };
            db.Applications.Add(application);
            await db.SaveChangesAsync();
            return application.ApplicationId;
        });
    }

    private static string Url(int id) => $"/api/applications/{id}/keyword-score";

    [Theory]
    [InlineData(ExtractionMethods.PdfText)]
    [InlineData(ExtractionMethods.PdfOcr)]
    [InlineData(ExtractionMethods.ImageOcr)]
    public async Task Completed_cv_returns_real_score_from_job_skills_for_any_extraction_method(string method)
    {
        using var env = await CreateAsync();
        var id = await AddApplicationAsync(env, AiJob, ExtractionStatuses.Completed,
            "GPA 3.10. Lập trình Python cơ bản, phân tích dữ liệu bằng Excel. Có tham gia CLB lập trình.", method);

        var body = await env.Client.GetFromJsonAsync<KeywordPreviewDto>(Url(id));

        Assert.Equal(id, body!.ApplicationId);
        Assert.Equal("CV_Demo.pdf", body.FileName);
        Assert.Equal("AI Engineer Intern", body.JobTitle);
        Assert.Equal(33.33m, body.KeywordScore);
        Assert.Equal(3.10m, body.Gpa);
        Assert.Null(body.YearsExperience);
        Assert.Equal(6, body.Skills.Count);
        Assert.Equal([SkillTypes.MustHave, SkillTypes.MustHave, SkillTypes.MustHave], body.Skills.Take(3).Select(s => s.SkillType));

        var python = body.Skills.Single(s => s.SkillCode == "python");
        Assert.True(python.IsMatched);
        Assert.Contains("Lập trình Python cơ bản", python.Evidence);
        Assert.Contains("Thiếu kỹ năng bắt buộc: LLM API, Machine Learning.", body.Notes);
        Assert.Contains("Vị trí không yêu cầu số năm kinh nghiệm.", body.Notes);
    }

    [Fact]
    public async Task Aliases_are_read_from_the_job_only()
    {
        using var env = await CreateAsync();
        const string text = "GPA 2.90. 6 tháng thực tập lập trình C# và ASP.NET, viết truy vấn T-SQL. Dùng GitHub quản lý mã nguồn.";
        var backend = await AddApplicationAsync(env, BackendJob, ExtractionStatuses.Completed, text);
        var ai = await AddApplicationAsync(env, AiJob, ExtractionStatuses.Completed, "Có kinh nghiệm với MySQL.");

        var body = await env.Client.GetFromJsonAsync<KeywordPreviewDto>(Url(backend));
        Assert.Equal(86.67m, body!.KeywordScore);                                 // kết quả hồ sơ 2 trong tài liệu
        Assert.Equal(0.5m, body.YearsExperience);
        Assert.True(body.Skills.Single(s => s.SkillCode == "sqlserver").IsMatched);   // alias T-SQL của job 1
        Assert.True(body.Skills.Single(s => s.SkillCode == "git").IsMatched);          // alias GitHub
        Assert.False(body.Skills.Single(s => s.SkillCode == "docker").IsMatched);

        var aiBody = await env.Client.GetFromJsonAsync<KeywordPreviewDto>(Url(ai));
        Assert.True(aiBody!.Skills.Single(s => s.SkillCode == "sql").IsMatched);       // alias MySQL của job 3
    }

    [Fact]
    public async Task Unknown_application_returns_404()
    {
        using var env = await CreateAsync();
        using var response = await env.Client.GetAsync(Url(999_999));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(ExtractionStatuses.Pending, null)]
    [InlineData(ExtractionStatuses.Failed, null)]
    [InlineData(ExtractionStatuses.Completed, "")]
    [InlineData(ExtractionStatuses.Completed, "   ")]
    // Đọc lại (A7) đưa CV về Pending nhưng còn chữ cũ: không chấm trên chữ cũ
    [InlineData(ExtractionStatuses.Pending, "Python GPA 3.5")]
    [InlineData(ExtractionStatuses.Failed, "Python GPA 3.5")]
    public async Task Cv_not_ready_returns_409_with_status_and_no_score(string status, string? text)
    {
        using var env = await CreateAsync();
        var id = await AddApplicationAsync(env, AiJob, status, text);

        using var response = await env.Client.GetAsync(Url(id));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(status, json.RootElement.GetProperty("extractionStatus").GetString());
        Assert.Contains($"ExtractionStatus = {status}", json.RootElement.GetProperty("detail").GetString());
        Assert.False(json.RootElement.TryGetProperty("keywordScore", out _));
    }

    [Fact]
    public async Task Preview_writes_nothing()
    {
        using var env = await CreateAsync();
        var id = await AddApplicationAsync(env, AiJob, ExtractionStatuses.Completed, "Python");

        await env.Client.GetFromJsonAsync<KeywordPreviewDto>(Url(id));

        Assert.Equal(0, await env.DbAsync(db => db.Evaluations.CountAsync()));
        Assert.Equal(0, await env.DbAsync(db => db.EvaluationSkills.CountAsync()));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Endpoint_does_not_exist_outside_development(string environment)
    {
        using var env = await CreateAsync(environment);
        var id = await AddApplicationAsync(env, AiJob, ExtractionStatuses.Completed, "Python");

        using var response = await env.Client.GetAsync(Url(id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Endpoint_is_off_when_disabled_in_development()
    {
        using var factory = TestSupport.ApiFactory(settings: new() { ["CvUpload:Endpoint:Enabled"] = "false" });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Url(1));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Appsettings_keep_keyword_30_ai_70_and_inner_weights()
    {
        using var env = await CreateAsync();
        var o = env.Factory.Services.GetRequiredService<IOptions<ScoringOptions>>().Value;

        Assert.Equal((0.3m, 0.7m, 70m), (o.KeywordWeight, o.AiWeight, o.PassThreshold));
        Assert.Equal((0.6m, 0.2m, 0.1m, 0.1m), (o.KeywordMustHaveWeight, o.KeywordNiceToHaveWeight, o.KeywordGpaWeight, o.KeywordExperienceWeight));
    }

    [Fact]
    public void Invalid_weights_stop_the_application_at_startup()
    {
        using var factory = TestSupport.ApiFactory(settings: new() { ["Scoring:KeywordWeight"] = "0.5" });
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }
}
