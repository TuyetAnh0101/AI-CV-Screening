using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartCv.Core.Ai;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Extraction;
using SmartCv.Core.Options;
using SmartCv.Core.Storage;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Extraction;
using SmartCv.Infrastructure.Scoring;

namespace SmartCv.Tests;

/// <summary>HTTP → persisted settings → scoring → HTTP, without SQL Server or a Gemini call.</summary>
public class ScoringConsistencyEndpointTests
{
    private const string FullCv = "GPA 3.5. Python, Machine Learning, LLM API, Prompt Engineering, SQL, NLP.";
    private sealed record CreatedRunSnapshot(int RunId, decimal KeywordWeight, decimal AiWeight);

    private sealed class FixedAi : IAiEvaluator
    {
        public int Calls { get; private set; }

        public Task<AiEvaluationResult> EvaluateAsync(AiEvaluationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new AiEvaluationResult { IsSuccess = true, Score = 60m, Reasoning = "Synthetic AI response." });
        }
    }

    private static WebApplicationFactory<Program> Factory(bool customWeights = false)
    {
        var settings = new Dictionary<string, string?> { ["Scoring:WorkerEnabled"] = "false" };
        if (customWeights)
        {
            settings["Scoring:KeywordWeight"] = "0.4";
            settings["Scoring:AiWeight"] = "0.6";
        }
        return TestSupport.ApiFactory(settings: settings).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAiEvaluator>();
            services.AddSingleton<IAiEvaluator>(new FixedAi());
        }));
    }

    private static async Task<(int PromptId, int SetId, int[] ApplicationIds)> SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        await db.Database.EnsureCreatedAsync();
        var prompt = new PromptVersion { VersionName = "consistency", SystemPrompt = "Synthetic prompt.", IsActive = true };
        var set = new EvaluationSet { Name = "Synthetic consistency set" };
        var applications = Enumerable.Range(0, 2).Select(i => new Application
        {
            JobId = 3,
            Cv = new Cv
            {
                CvId = Guid.NewGuid(), OriginalFileName = $"synthetic-{i}.pdf", StoredPath = "synthetic",
                ContentType = "application/pdf", FileHash = Guid.NewGuid().ToString("N").PadRight(64, '0'),
                ExtractionStatus = ExtractionStatuses.Completed, ExtractedText = FullCv
            }
        }).ToArray();
        db.AddRange(prompt, set);
        db.Applications.AddRange(applications);
        await db.SaveChangesAsync();
        db.HrLabels.AddRange(applications.Select((a, i) => new HrLabel
        {
            ApplicationId = a.ApplicationId, EvaluationSetId = set.EvaluationSetId, IsPass = i == 0,
            LabeledAt = DateTime.UtcNow
        }));
        await db.SaveChangesAsync();
        return (prompt.PromptVersionId, set.EvaluationSetId, applications.Select(a => a.ApplicationId).ToArray());
    }

    [Theory]
    [InlineData(false, false, 72)]
    [InlineData(false, true, 72)]
    [InlineData(true, false, 76)]
    [InlineData(true, true, 76)]
    public async Task Both_run_types_score_with_saved_weights_and_return_the_same_snapshot_after_defaults_change(
        bool customWeights, bool promptTest, int expectedScore)
    {
        using var factory = Factory(customWeights);
        using var client = factory.CreateClient();
        var ids = await SeedAsync(factory.Services);
        using var response = promptTest
            ? await client.PostAsJsonAsync("/api/prompt-tests", new StartPromptTestRequest { PromptVersionId = ids.PromptId, EvaluationSetId = ids.SetId })
            : await client.PostAsJsonAsync("/api/runs", new StartRunRequest { ApplicationIds = ids.ApplicationIds.ToList() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreatedRunSnapshot>();
        Assert.NotNull(created);
        var keywordWeight = customWeights ? .4m : .3m;
        var aiWeight = customWeights ? .6m : .7m;
        Assert.Equal((keywordWeight, aiWeight), (created.KeywordWeight, created.AiWeight));

        var ai = new FixedAi();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            var saved = await db.EvaluationRuns.AsNoTracking().SingleAsync(r => r.RunId == created.RunId);
            Assert.Equal((keywordWeight, aiWeight), (saved.KeywordWeight, saved.AiWeight));
            // Only Keyword/AI defaults change; internal keyword weights and cap retain the original values.
            var changed = new ScoringOptions { KeywordWeight = .9m, AiWeight = .1m, PassThreshold = 99m, MissingMustHaveCap = 69m };
            var processor = new RunScoringProcessor(db, ai, Options.Create(changed), NullLogger<RunScoringProcessor>.Instance);
            Assert.Equal(2, await processor.ProcessRunAsync(created.RunId, default));
            Assert.Equal(0, await processor.ProcessRunAsync(created.RunId, default)); // Completed history is not rescored.
        }
        Assert.Equal(2, ai.Calls);

        var progress = await client.GetFromJsonAsync<RunProgressDto>($"/api/runs/{created.RunId}");
        Assert.NotNull(progress);
        Assert.Equal((RunStatuses.Completed, 2, 0, keywordWeight, aiWeight),
            (progress.Status, progress.ProcessedItems, progress.FailedItems, progress.KeywordWeight, progress.AiWeight));
        Assert.Equal(2, progress.Items.Count);
        foreach (var item in progress.Items)
        {
            var detail = await client.GetFromJsonAsync<EvaluationDetailDto>($"/api/evaluations/{item.EvaluationId}");
            Assert.NotNull(detail);
            Assert.Equal((100m, 60m, (decimal)expectedScore, true),
                (detail.KeywordScore, detail.AiScore, detail.FinalScore, detail.PredictedPass));
            Assert.Equal((keywordWeight, aiWeight, 70m), (detail.KeywordWeight, detail.AiWeight, detail.PassThreshold));
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            var evaluation = await db.Evaluations.AsNoTracking().SingleAsync(e => e.EvaluationId == item.EvaluationId);
            Assert.Equal((evaluation.FinalScore, evaluation.PredictedPass), (detail.FinalScore, detail.PredictedPass));
        }
        if (promptTest)
        {
            var accuracy = await client.GetFromJsonAsync<PromptTestRunDto>($"/api/prompt-tests/{created.RunId}");
            Assert.Equal(50m, accuracy!.AccuracyPercent); // one true and one false HR label, both predicted pass
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preview_exposes_the_actual_weights_threshold_and_cap_without_creating_history(bool customWeights)
    {
        using var factory = Factory(customWeights);
        using var client = factory.CreateClient();
        var ids = await SeedAsync(factory.Services);
        using var response = await client.PostAsync($"/api/applications/{ids.ApplicationIds[0]}/ai-preview", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<AiPreviewDto>();
        Assert.NotNull(preview);
        Assert.Equal((customWeights ? .4m : .3m, customWeights ? .6m : .7m, 70m, (decimal?)69m),
            (preview.KeywordWeight, preview.AiWeight, preview.PassThreshold, preview.MissingMustHaveCap));
        Assert.Equal(Math.Round(preview.KeywordScore * preview.KeywordWeight + preview.AiScore!.Value * preview.AiWeight,
            2, MidpointRounding.AwayFromZero), preview.FinalScore);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<SmartCvDbContext>().EvaluationRuns.ToListAsync());
    }

    private sealed class EmptyExtractor(bool reportsFailure) : ICvTextExtractor
    {
        public Task<TextExtractionResult> ExtractAsync(Stream content, string contentType, CancellationToken cancellationToken = default) =>
            Task.FromResult(reportsFailure
                ? TextExtractionResult.Failure("Không nhận ra chữ trong file.", ExtractionMethods.PdfOcr)
                : TextExtractionResult.Success(" \r\n ", ExtractionMethods.PdfText, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uploaded_cv_without_extracted_text_finishes_failed_without_scores_or_ai_and_does_not_hang(bool extractionFailure)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        await SeedAsync(factory.Services);
        using var upload = new MultipartFormDataContent
        {
            { new ByteArrayContent(TestSupport.Pdf("synthetic-empty-content")), "files", "empty-content.pdf" }
        };
        using var uploaded = await client.PostAsync("/api/cvs/upload", upload);
        var uploadDto = await uploaded.Content.ReadFromJsonAsync<CvUploadResponse>();
        Assert.NotNull(uploadDto);
        var file = Assert.Single(uploadDto.Files);
        Assert.Equal(CvUploadStatuses.Accepted, file.Status);
        var cvId = file.CvId!.Value;
        using var assignment = await client.PostAsJsonAsync("/api/applications", new CreateApplicationsRequest { JobId = 3, CvIds = [cvId] });
        Assert.Equal(HttpStatusCode.OK, assignment.StatusCode);
        var applications = await assignment.Content.ReadFromJsonAsync<CreateApplicationsResponse>();
        var applicationId = Assert.Single(applications!.Items).ApplicationId!.Value;
        using var started = await client.PostAsJsonAsync("/api/runs", new StartRunRequest { ApplicationIds = [applicationId] });
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var run = await started.Content.ReadFromJsonAsync<RunProgressDto>();
        var ai = new FixedAi();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            var scoring = new RunScoringProcessor(db, ai, Options.Create(new ScoringOptions()), NullLogger<RunScoringProcessor>.Instance);
            Assert.Equal(0, await scoring.ProcessRunAsync(run!.RunId, default)); // Awaiting extraction is expected.
            var extraction = new CvExtractionProcessor(db, scope.ServiceProvider.GetRequiredService<ICvFileStorage>(),
                new EmptyExtractor(extractionFailure), NullLogger<CvExtractionProcessor>.Instance);
            await extraction.ProcessAsync(cvId, default);
            Assert.Equal(1, await scoring.ProcessRunAsync(run.RunId, default));
            Assert.Equal(0, await scoring.ProcessRunAsync(run.RunId, default));
        }
        Assert.Equal(0, ai.Calls);
        var progress = await client.GetFromJsonAsync<RunProgressDto>($"/api/runs/{run!.RunId}");
        Assert.Equal((RunStatuses.Completed, 1, 1, 100m),
            (progress!.Status, progress.ProcessedItems, progress.FailedItems, progress.Percent));
        var item = Assert.Single(progress.Items);
        Assert.Equal(EvaluationStatuses.Failed, item.Status);
        Assert.Null(item.KeywordScore);
        Assert.Null(item.AiScore);
        Assert.Null(item.FinalScore);
        Assert.Null(item.PredictedPass);
        Assert.False(string.IsNullOrWhiteSpace(item.ErrorMessage));
    }
}
