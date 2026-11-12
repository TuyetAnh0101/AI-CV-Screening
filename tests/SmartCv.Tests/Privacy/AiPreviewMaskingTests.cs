using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Ai;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Ai;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

/// <summary>
/// A37 qua pipeline HTTP thật: POST /api/applications/{id}/ai-preview → GeminiEvaluationService → HttpMessageHandler giả.
/// Tên lấy từ bảng Candidates; payload gửi Gemini đã che; keyword và database vẫn dùng nội dung gốc.
/// </summary>
public class AiPreviewMaskingTests
{
    private const int BackendJob = 1;
    private const string CandidateName = "Trần Minh Khoa";

    // Tên chỉ xuất hiện giữa CV (dòng đầu là chức danh): chỉ che được nhờ tên trong bảng Candidates.
    private const string CvText =
        "Backend Developer\n" +
        "Tôi là Trần Minh Khoa. Email: khoa.tran@example.com | SĐT: 0900 123 456 | github.com/tran-minh-khoa\n" +
        "GPA 3.2. 1.5 năm làm C#, ASP.NET Core, SQL Server, EF Core, Git, Docker. CI/CD bằng GitHub Actions.";

    private sealed record Env(WebApplicationFactory<Program> Factory, HttpClient Client, GeminiPayloadCapture Handler) : IDisposable
    {
        public void Dispose() { Client.Dispose(); Factory.Dispose(); }

        public async Task<T> DbAsync<T>(Func<SmartCvDbContext, Task<T>> action)
        {
            using var scope = Factory.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<SmartCvDbContext>());
        }
    }

    private static async Task<Env> CreateAsync(bool? mask)
    {
        var handler = new GeminiPayloadCapture();
        var settings = new Dictionary<string, string?> { ["Gemini:MinDelayBetweenCallsMs"] = "0" };
        if (mask is not null) settings["Gemini:MaskPersonalData"] = mask.Value ? "true" : "false";
        var factory = TestSupport.ApiFactory(settings: settings).WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddHttpClient<GeminiEvaluationService>().ConfigurePrimaryHttpMessageHandler(() => handler)));
        var env = new Env(factory, factory.CreateClient(), handler);
        await env.DbAsync(async db =>
        {
            await db.Database.EnsureCreatedAsync();
            db.PromptVersions.Add(new PromptVersion { VersionName = "v-a37", SystemPrompt = "Bạn là HR.", IsActive = true, CreatedAt = DateTime.UtcNow });
            return await db.SaveChangesAsync();
        });
        return env;
    }

    private static Task<int> AddApplicationAsync(Env env, string? candidateName) => env.DbAsync(async db =>
    {
        Candidate? candidate = candidateName is null ? null : new Candidate { FullName = candidateName, CreatedAt = DateTime.UtcNow };
        if (candidate is not null) db.Candidates.Add(candidate);
        var id = Guid.NewGuid();
        db.Cvs.Add(new Cv
        {
            CvId = id, Candidate = candidate, OriginalFileName = "CV.pdf", StoredPath = $"Uploads/CV/2026/11/{id}.pdf", ContentType = "application/pdf",
            FileSizeBytes = 1, FileHash = id.ToString("N").PadRight(64, '0').ToUpperInvariant(), ExtractionStatus = ExtractionStatuses.Completed,
            ExtractedText = CvText, UploadedAt = DateTime.UtcNow
        });
        var application = new Application { CvId = id, JobId = BackendJob, CreatedAt = DateTime.UtcNow };
        db.Applications.Add(application);
        await db.SaveChangesAsync();
        return application.ApplicationId;
    });

    private static async Task<AiPreviewDto> PreviewAsync(Env env, int id)
    {
        using var response = await env.Client.PostAsync($"/api/applications/{id}/ai-preview", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AiPreviewDto>())!;
    }

    [Fact]
    public async Task Preview_sends_masked_payload_using_candidate_name_from_database()
    {
        using var env = await CreateAsync(mask: null);   // cấu hình mặc định
        var id = await AddApplicationAsync(env, CandidateName);

        var preview = await PreviewAsync(env, id);

        Assert.True(preview.AiSucceeded);
        Assert.False(preview.AiSimulated);
        var user = GeminiPayloadCapture.UserText(Assert.Single(env.Handler.Bodies));
        foreach (var raw in new[] { CandidateName, "khoa.tran@example.com", "0900 123 456", "github.com/tran-minh-khoa" })
            Assert.DoesNotContain(raw, user);
        Assert.Contains("Tôi là [CANDIDATE]. Email: [EMAIL] | SĐT: [PHONE] | [LINK]", user);
        Assert.Contains("GitHub Actions", user);
        Assert.Contains(AiPromptBuilder.MaskedDataRule, user);

        // Database vẫn giữ nội dung gốc
        Assert.Equal(CvText, await env.DbAsync(db => db.Applications.Where(a => a.ApplicationId == id).Select(a => a.Cv.ExtractedText).SingleAsync()));
    }

    [Fact]
    public async Task Preview_without_masking_sends_original_text_and_scores_are_unchanged()
    {
        using var masked = await CreateAsync(mask: true);
        using var plain = await CreateAsync(mask: false);
        var maskedPreview = await PreviewAsync(masked, await AddApplicationAsync(masked, CandidateName));
        var plainPreview = await PreviewAsync(plain, await AddApplicationAsync(plain, CandidateName));

        var plainUser = GeminiPayloadCapture.UserText(Assert.Single(plain.Handler.Bodies));
        Assert.Contains(CandidateName, plainUser);
        Assert.Contains("khoa.tran@example.com", plainUser);
        Assert.DoesNotContain(AiPromptBuilder.MaskedDataRule, plainUser);

        // Keyword dùng nội dung gốc trong cả hai trường hợp; AI giả trả cùng điểm → toàn bộ điểm giống nhau.
        Assert.Equal(plainPreview.KeywordScore, maskedPreview.KeywordScore);
        Assert.Equal(plainPreview.AiScore, maskedPreview.AiScore);
        Assert.Equal(plainPreview.FinalScore, maskedPreview.FinalScore);
        Assert.Equal(plainPreview.Skills.Select(s => (s.SkillCode, s.IsMatched)), maskedPreview.Skills.Select(s => (s.SkillCode, s.IsMatched)));
    }

    [Fact]
    public async Task Preview_without_candidate_record_still_masks_contact_data()
    {
        using var env = await CreateAsync(mask: null);
        var id = await AddApplicationAsync(env, candidateName: null);

        await PreviewAsync(env, id);

        var user = GeminiPayloadCapture.UserText(Assert.Single(env.Handler.Bodies));
        Assert.Contains("Email: [EMAIL] | SĐT: [PHONE] | [LINK]", user);
        Assert.DoesNotContain("example.com", user);
    }
}
