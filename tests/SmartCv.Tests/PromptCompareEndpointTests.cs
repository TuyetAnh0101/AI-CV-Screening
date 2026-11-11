using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

/// <summary>Dữ liệu tổng hợp giống seed_demo.sql (10 CV, v1 70% / v2 80%). Dùng cho test InMemory và SQL A17.</summary>
internal static class PromptCompareSeed
{
    public static readonly string[] Names =
    [
        "Nguyễn Văn An", "Trần Thị Bình", "Lê Hoàng Cường", "Phạm Minh Dũng", "Hoàng Thị Em",
        "Võ Quốc Phong", "Đặng Thu Giang", "Bùi Thanh Hải", "Đỗ Ngọc Hân", "Ngô Gia Khánh"
    ];
    public static readonly bool[] Hr = [true, true, false, true, false, true, false, true, false, true];
    public static readonly decimal[] V1Scores = [91.60m, 64.60m, 27.50m, 93.00m, 72.50m, 94.40m, 57.40m, 87.40m, 75.60m, 92.30m];
    public static readonly decimal[] V2Scores = [93.00m, 66.00m, 24.00m, 94.40m, 72.50m, 95.80m, 38.50m, 89.50m, 60.20m, 93.70m];
    private static readonly int[] JobIndex = [0, 0, 0, 1, 1, 2, 2, 3, 3, 4];
    public static readonly DateTime Day = new(2026, 11, 4, 14, 0, 0, DateTimeKind.Utc);

    public sealed record Data(int Set, int PromptV1, int PromptV2, int RunV1, int RunV2, int[] Apps);

    public static async Task<Data> DemoAsync(SmartCvDbContext db)
    {
        var jobs = await db.Jobs.OrderBy(j => j.JobId).Select(j => j.JobId).Take(5).ToListAsync();
        var apps = Names.Select((name, i) => NewApplication(jobs[JobIndex[i]], name)).ToArray();
        var v1 = new PromptVersion { VersionName = "v1-co-ban", SystemPrompt = "Tổng hợp v1", CreatedAt = Day.AddDays(-3) };
        var v2 = new PromptVersion { VersionName = "v2-tieu-chi-ro-rang", SystemPrompt = "Tổng hợp v2", IsActive = true, CreatedAt = Day.AddDays(-1) };
        var set = new EvaluationSet { Name = "Bộ CV đánh giá tổng hợp A17", CreatedAt = Day.AddHours(-5) };
        db.AddRange(v1, v2, set);
        db.AddRange(apps);
        await db.SaveChangesAsync();

        var ids = apps.Select(a => a.ApplicationId).ToArray();
        for (var i = 0; i < ids.Length; i++)
            db.HrLabels.Add(Label(set.EvaluationSetId, ids[i], Hr[i], $"Ghi chú HR {i + 1}"));
        var r1 = Run(v1.PromptVersionId, set.EvaluationSetId, ids, V1Scores, Day, Day.AddMinutes(6));
        var r2 = Run(v2.PromptVersionId, set.EvaluationSetId, ids, V2Scores, Day.AddHours(1), Day.AddHours(1).AddMinutes(5.5));
        db.EvaluationRuns.Add(r1);
        await db.SaveChangesAsync();
        db.EvaluationRuns.Add(r2);
        await db.SaveChangesAsync();
        return new(set.EvaluationSetId, v1.PromptVersionId, v2.PromptVersionId, r1.RunId, r2.RunId, ids);
    }

    public static Application NewApplication(int jobId, string name)
    {
        var id = Guid.NewGuid();
        return new Application
        {
            JobId = jobId, CreatedAt = Day.AddDays(-2), Cv = new Cv
            {
                CvId = id, OriginalFileName = $"CV_{name.Replace(" ", string.Empty)}.pdf", StoredPath = "synthetic/" + id.ToString("N"),
                ContentType = "application/pdf", FileSizeBytes = 100, FileHash = id.ToString("N").PadRight(64, '0').ToUpperInvariant(),
                ExtractedText = "Synthetic CV.", ExtractionStatus = "Completed", ExtractionMethod = "PdfText", UploadedAt = Day.AddDays(-2),
                Candidate = new Candidate { FullName = name, CreatedAt = Day.AddDays(-2) }
            }
        };
    }

    public static HrLabel Label(int setId, int applicationId, bool isPass, string? note = null) => new()
    {
        EvaluationSetId = setId, ApplicationId = applicationId, IsPass = isPass, Note = note,
        LabeledBy = "HR tổng hợp", LabeledAt = Day.AddHours(-4)
    };

    /// <summary>Lượt PromptTest đã chấm xong: PredictedPass = điểm cuối ≥ ngưỡng (A10).</summary>
    public static EvaluationRun Run(int prompt, int setId, IReadOnlyList<int> apps, IReadOnlyList<decimal> scores,
        DateTime createdAt, DateTime? completedAt, string status = "Completed", string model = "gemini-2.5-flash",
        decimal temperature = 0.20m, decimal keywordWeight = 0.30m, decimal aiWeight = 0.70m, decimal threshold = 70m) => new()
    {
        RunType = "PromptTest", PromptVersionId = prompt, EvaluationSetId = setId, ModelName = model, Temperature = temperature,
        KeywordWeight = keywordWeight, AiWeight = aiWeight, PassThreshold = threshold, Status = status,
        TotalItems = apps.Count, ProcessedItems = status == "Completed" ? apps.Count : 0, CreatedAt = createdAt, CompletedAt = completedAt,
        Evaluations = apps.Select((id, i) => new Evaluation
        {
            ApplicationId = id, Status = "Completed", KeywordScore = 50m, AiScore = scores[i], FinalScore = scores[i],
            PredictedPass = scores[i] >= threshold, ScoreSource = "Combined", FitLabel = "Synthetic",
            Reasoning = "Kết quả tổng hợp đã lưu", AiRawResponse = "{}", CreatedAt = createdAt
        }).ToList()
    };
}

public class PromptCompareEndpointTests
{
    private const string Summary =
        "v2-tieu-chi-ro-rang đúng 80% (8/10 CV), cao hơn v1-co-ban 10 điểm phần trăm (70%, 7/10 CV). " +
        "Sửa đúng thêm 1 CV, không làm sai thêm CV nào. Bỏ sót giữ nguyên (1 → 1), chọn nhầm giảm 1 (2 → 1).";

    private static WebApplicationFactory<Program> Factory(Dictionary<string, string?>? settings = null, string environment = "Development")
    {
        settings ??= [];
        settings["Scoring:WorkerEnabled"] = "false";
        settings["Gemini:UseFake"] = (environment == "Development").ToString();   // A10: AI giả chỉ được bật ở Development
        return TestSupport.ApiFactory(environment, settings)
            .WithWebHostBuilder(host => host.ConfigureLogging(logging => logging.ClearProviders()));
    }

    private static async Task<T> WithDbAsync<T>(WebApplicationFactory<Program> factory, Func<SmartCvDbContext, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        await db.Database.EnsureCreatedAsync();
        return await action(db);
    }

    private static Task<PromptCompareSeed.Data> SeedAsync(WebApplicationFactory<Program> factory) =>
        WithDbAsync(factory, PromptCompareSeed.DemoAsync);

    private static async Task<int> AddRunAsync(WebApplicationFactory<Program> factory, EvaluationRun run) =>
        await WithDbAsync(factory, async db =>
        {
            db.EvaluationRuns.Add(run);
            await db.SaveChangesAsync();
            return run.RunId;
        });

    private static string Versions(PromptCompareSeed.Data d) =>
        $"/api/prompts/compare?baseVersionId={d.PromptV1}&candidateVersionId={d.PromptV2}&evaluationSetId={d.Set}";

    private static async Task<PromptCompareDto> GetCompareAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} → {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<PromptCompareDto>())!;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetProblemAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        return (response.StatusCode, json.RootElement.Clone());
    }

    private static decimal[] Scores(Func<int, decimal> score) => Enumerable.Range(0, 10).Select(score).ToArray();

    [Fact]
    public async Task Version_compare_on_demo_data_is_better_by_10_points_with_fixed_cv_first()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var d = await SeedAsync(factory);

        var c = await GetCompareAsync(client, Versions(d));

        Assert.Equal("Better", c.Verdict);
        Assert.Equal(10m, c.AccuracyDelta);
        Assert.Equal((1, 0, -1), (c.CorrectDelta, c.MissedDelta, c.WrongPickDelta));
        Assert.Equal((1, 0, 9, 0), (c.FixedCount, c.BrokenCount, c.UnchangedCount, c.UnscoredCount));
        Assert.True(c.IsFairComparison);
        Assert.Empty(c.Warnings);
        Assert.Equal(Summary, c.Summary);

        Assert.Equal((d.RunV1, d.PromptV1, "v1-co-ban", d.Set, "Completed"),
            (c.Base.RunId, c.Base.PromptVersionId, c.Base.PromptVersionName, c.Base.EvaluationSetId, c.Base.Status));
        Assert.Equal((70m, 7, 1, 2), (c.Base.AccuracyPercent!.Value, c.Base.CorrectCount, c.Base.MissedCount, c.Base.WrongPickCount));
        Assert.Equal((d.RunV2, 80m, 8, 1, 1),
            (c.Candidate.RunId, c.Candidate.AccuracyPercent!.Value, c.Candidate.CorrectCount, c.Candidate.MissedCount, c.Candidate.WrongPickCount));
        Assert.Equal(DateTimeKind.Utc, c.Base.CompletedAt!.Value.Kind);

        Assert.Equal(10, c.Items.Count);
        var first = c.Items[0];
        Assert.Equal(("Đỗ Ngọc Hân", d.Apps[8], "Fixed", false), (first.CandidateName, first.ApplicationId, first.Change, first.HrIsPass));
        Assert.Equal((true, 75.60m, false), (first.BasePredictedPass, first.BaseScore, first.BaseIsMatch));
        Assert.Equal((false, 60.20m, true), (first.CandidatePredictedPass, first.CandidateScore, first.CandidateIsMatch));
        Assert.Equal(-15.40m, first.ScoreDelta);
        Assert.Equal("Ghi chú HR 9", first.HrNote);
        Assert.Equal("CV_ĐỗNgọcHân.pdf", first.FileName);
        Assert.NotNull(first.BaseEvaluationId);
        Assert.NotNull(first.CandidateEvaluationId);
        Assert.NotEqual(first.BaseEvaluationId, first.CandidateEvaluationId);
        Assert.All(c.Items.Skip(1), i => Assert.Equal("Unchanged", i.Change));
        // Thứ tự xác định: CV thay đổi trước, rồi theo job, rồi ApplicationId
        var rest = c.Items.Skip(1).ToList();
        Assert.Equal(rest.OrderBy(i => i.JobTitle, StringComparer.Ordinal).ThenBy(i => i.ApplicationId).Select(i => i.ApplicationId),
            rest.Select(i => i.ApplicationId));
    }

    [Fact]
    public async Task Response_json_matches_the_frontend_contract()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var d = await SeedAsync(factory);

        using var json = JsonDocument.Parse(await client.GetStringAsync(Versions(d)));
        var root = json.RootElement;
        foreach (var name in new[] { "base", "candidate", "accuracyDelta", "correctDelta", "missedDelta", "wrongPickDelta",
                     "fixedCount", "brokenCount", "unchangedCount", "unscoredCount", "verdict", "summary",
                     "isFairComparison", "warnings", "items" })
            Assert.True(root.TryGetProperty(name, out _), $"Thiếu trường {name}");
        Assert.Equal(JsonValueKind.Array, root.GetProperty("warnings").ValueKind);
        Assert.Equal("Better", root.GetProperty("verdict").GetString());
        foreach (var name in new[] { "runId", "promptVersionId", "promptVersionName", "evaluationSetId", "evaluationSetName",
                     "modelName", "status", "totalItems", "accuracyPercent", "correctCount", "missedCount", "wrongPickCount", "createdAt", "completedAt" })
            Assert.True(root.GetProperty("base").TryGetProperty(name, out _), $"Thiếu trường base.{name}");
        var item = root.GetProperty("items")[0];
        foreach (var name in new[] { "applicationId", "candidateName", "fileName", "jobTitle", "hrIsPass", "hrNote",
                     "baseEvaluationId", "basePredictedPass", "baseScore", "baseIsMatch",
                     "candidateEvaluationId", "candidatePredictedPass", "candidateScore", "candidateIsMatch", "scoreDelta", "change" })
            Assert.True(item.TryGetProperty(name, out _), $"Thiếu trường items[].{name}");
        Assert.Equal("Fixed", item.GetProperty("change").GetString());
    }

    [Fact]
    public async Task Run_compare_matches_version_compare_for_the_same_runs()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var d = await SeedAsync(factory);

        var byVersion = await GetCompareAsync(client, Versions(d));
        var byRun = await GetCompareAsync(client, $"/api/prompt-tests/compare?baseRunId={d.RunV1}&candidateRunId={d.RunV2}");

        Assert.Equal(JsonSerializer.Serialize(byVersion), JsonSerializer.Serialize(byRun));
        var reversed = await GetCompareAsync(client, $"/api/prompt-tests/compare?baseRunId={d.RunV2}&candidateRunId={d.RunV1}");
        Assert.Equal(("Worse", -10m, 0, 1), (reversed.Verdict, reversed.AccuracyDelta!.Value, reversed.FixedCount, reversed.BrokenCount));
    }

    [Fact]
    public async Task Version_compare_uses_latest_completed_run_and_never_unfinished_or_screening_runs()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var d = await SeedAsync(factory);
        var allWrong = Scores(i => PromptCompareSeed.Hr[i] ? 10m : 90m);
        var day = PromptCompareSeed.Day;

        // Lượt cũ hơn (đã xong trước run V2) và các lượt mới hơn nhưng chưa xong / lỗi / Screening
        await AddRunAsync(factory, PromptCompareSeed.Run(d.PromptV2, d.Set, d.Apps, allWrong, day.AddMinutes(30), day.AddMinutes(40)));
        foreach (var status in new[] { "Queued", "Running", "Failed" })
            await AddRunAsync(factory, PromptCompareSeed.Run(d.PromptV2, d.Set, d.Apps, allWrong, day.AddDays(1), null, status));
        await WithDbAsync(factory, async db =>
        {
            db.EvaluationRuns.Add(new EvaluationRun
            {
                RunType = "Screening", PromptVersionId = d.PromptV2, Status = "Completed", ModelName = "gemini-2.5-flash",
                KeywordWeight = .3m, AiWeight = .7m, PassThreshold = 70m, CreatedAt = day.AddDays(2), CompletedAt = day.AddDays(2)
            });
            return await db.SaveChangesAsync();
        });
        Assert.Equal(d.RunV2, (await GetCompareAsync(client, Versions(d))).Candidate.RunId);

        // Lượt mới hoàn thành sau cùng được chọn; trùng CompletedAt thì RunId lớn hơn thắng
        var newer = await AddRunAsync(factory, PromptCompareSeed.Run(d.PromptV2, d.Set, d.Apps, PromptCompareSeed.V1Scores,
            day.AddDays(3), day.AddDays(3).AddMinutes(5)));
        var tie = await AddRunAsync(factory, PromptCompareSeed.Run(d.PromptV2, d.Set, d.Apps, PromptCompareSeed.V2Scores,
            day.AddDays(3), day.AddDays(3).AddMinutes(5)));
        Assert.True(tie > newer);
        var latest = await GetCompareAsync(client, Versions(d));
        Assert.Equal(tie, latest.Candidate.RunId);
        Assert.Equal(d.RunV1, latest.Base.RunId);
        Assert.Equal("Better", latest.Verdict);
    }

    [Theory]
    [InlineData("/api/prompt-tests/compare")]
    [InlineData("/api/prompt-tests/compare?baseRunId=1")]
    [InlineData("/api/prompt-tests/compare?candidateRunId=2")]
    [InlineData("/api/prompt-tests/compare?baseRunId=0&candidateRunId=2")]
    [InlineData("/api/prompt-tests/compare?baseRunId=-1&candidateRunId=2")]
    [InlineData("/api/prompt-tests/compare?baseRunId=abc&candidateRunId=2")]
    [InlineData("/api/prompts/compare")]
    [InlineData("/api/prompts/compare?baseVersionId=1&candidateVersionId=2")]
    [InlineData("/api/prompts/compare?baseVersionId=1&evaluationSetId=1")]
    [InlineData("/api/prompts/compare?candidateVersionId=2&evaluationSetId=1")]
    [InlineData("/api/prompts/compare?baseVersionId=0&candidateVersionId=2&evaluationSetId=1")]
    [InlineData("/api/prompts/compare?baseVersionId=1&candidateVersionId=2&evaluationSetId=0")]
    public async Task Missing_or_invalid_parameters_return_400(string url)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Same_run_or_same_version_returns_400()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var d = await SeedAsync(factory);

        var sameRun = await GetProblemAsync(client, $"/api/prompt-tests/compare?baseRunId={d.RunV1}&candidateRunId={d.RunV1}");
        Assert.Equal((HttpStatusCode.BadRequest, "Cùng một lượt chạy"), (sameRun.Status, sameRun.Body.GetProperty("title").GetString()));
        var sameVersion = await GetProblemAsync(client,
            $"/api/prompts/compare?baseVersionId={d.PromptV2}&candidateVersionId={d.PromptV2}&evaluationSetId={d.Set}");
        Assert.Equal((HttpStatusCode.BadRequest, "Cùng một phiên bản"), (sameVersion.Status, sameVersion.Body.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task Unknown_run_returns_404_and_screening_run_returns_400()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var d = await SeedAsync(factory);
        var screening = await WithDbAsync(factory, async db =>
        {
            var run = new EvaluationRun
            {
                RunType = "Screening", PromptVersionId = d.PromptV2, Status = "Completed", ModelName = "gemini-2.5-flash",
                KeywordWeight = .3m, AiWeight = .7m, PassThreshold = 70m, CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow
            };
            db.EvaluationRuns.Add(run);
            await db.SaveChangesAsync();
            return run.RunId;
        });

        var missingBase = await GetProblemAsync(client, $"/api/prompt-tests/compare?baseRunId=999999&candidateRunId={d.RunV2}");
        Assert.Equal(HttpStatusCode.NotFound, missingBase.Status);
        Assert.Contains("#999999", missingBase.Body.GetProperty("detail").GetString());
        var missingCandidate = await GetProblemAsync(client, $"/api/prompt-tests/compare?baseRunId={d.RunV1}&candidateRunId=999999");
        Assert.Equal(HttpStatusCode.NotFound, missingCandidate.Status);
        var wrongType = await GetProblemAsync(client, $"/api/prompt-tests/compare?baseRunId={d.RunV1}&candidateRunId={screening}");
        Assert.Equal((HttpStatusCode.BadRequest, "Không phải lượt đánh giá prompt"),
            (wrongType.Status, wrongType.Body.GetProperty("title").GetString()));
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Running")]
    [InlineData("Failed")]
    public async Task Unfinished_run_returns_409_without_publishing_partial_numbers(string status)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var d = await SeedAsync(factory);
        var unfinished = await AddRunAsync(factory,
            PromptCompareSeed.Run(d.PromptV2, d.Set, d.Apps, PromptCompareSeed.V2Scores, DateTime.UtcNow, null, status));

        var result = await GetProblemAsync(client, $"/api/prompt-tests/compare?baseRunId={d.RunV1}&candidateRunId={unfinished}");

        Assert.Equal(HttpStatusCode.Conflict, result.Status);
        Assert.Equal("Lượt chạy chưa hoàn thành", result.Body.GetProperty("title").GetString());
        Assert.Equal(unfinished, result.Body.GetProperty("runId").GetInt32());
        Assert.Equal(409, result.Body.GetProperty("status").GetInt32());
        Assert.Equal(status, result.Body.GetProperty("runStatus").GetString());
        Assert.False(result.Body.TryGetProperty("verdict", out _));
    }

    [Fact]
    public async Task Unknown_version_or_set_returns_404()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var d = await SeedAsync(factory);

        var version = await GetProblemAsync(client, $"/api/prompts/compare?baseVersionId={d.PromptV1}&candidateVersionId=999999&evaluationSetId={d.Set}");
        Assert.Equal((HttpStatusCode.NotFound, "Không tìm thấy prompt"), (version.Status, version.Body.GetProperty("title").GetString()));
        var set = await GetProblemAsync(client, $"/api/prompts/compare?baseVersionId={d.PromptV1}&candidateVersionId={d.PromptV2}&evaluationSetId=999999");
        Assert.Equal((HttpStatusCode.NotFound, "Không tìm thấy bộ CV"), (set.Status, set.Body.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task Version_without_completed_run_returns_404_and_points_to_the_active_run()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var d = await SeedAsync(factory);
        var v3 = await WithDbAsync(factory, async db =>
        {
            var prompt = new PromptVersion { VersionName = "v3-thu", SystemPrompt = "Tổng hợp v3", CreatedAt = DateTime.UtcNow };
            db.PromptVersions.Add(prompt);
            await db.SaveChangesAsync();
            return prompt.PromptVersionId;
        });
        var url = $"/api/prompts/compare?baseVersionId={d.PromptV2}&candidateVersionId={v3}&evaluationSetId={d.Set}";

        var none = await GetProblemAsync(client, url);
        Assert.Equal((HttpStatusCode.NotFound, "Chưa có kết quả"), (none.Status, none.Body.GetProperty("title").GetString()));
        Assert.Contains("v3-thu", none.Body.GetProperty("detail").GetString());
        Assert.Equal(JsonValueKind.Null, none.Body.GetProperty("activeRunId").ValueKind);

        var running = await AddRunAsync(factory,
            PromptCompareSeed.Run(v3, d.Set, d.Apps, PromptCompareSeed.V2Scores, DateTime.UtcNow, null, "Running"));
        var active = await GetProblemAsync(client, url);
        Assert.Equal(HttpStatusCode.NotFound, active.Status);
        Assert.Equal(running, active.Body.GetProperty("activeRunId").GetInt32());
        Assert.Contains($"#{running}", active.Body.GetProperty("detail").GetString());
    }

    public static TheoryData<string, string, decimal, decimal, decimal, decimal> SettingChanges => new()
    {
        { "model", "gemini-2.5-pro", 0.20m, 0.30m, 0.70m, 70m },
        { "temperature", "gemini-2.5-flash", 0.70m, 0.30m, 0.70m, 70m },
        { "trọng số", "gemini-2.5-flash", 0.20m, 0.40m, 0.60m, 70m },
        { "ngưỡng Đạt", "gemini-2.5-flash", 0.20m, 0.30m, 0.70m, 75m }
    };

    [Theory]
    [MemberData(nameof(SettingChanges))]
    public async Task Different_run_settings_are_not_a_fair_comparison(string expected, string model, decimal temperature,
        decimal keywordWeight, decimal aiWeight, decimal threshold)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var d = await SeedAsync(factory);
        var other = await AddRunAsync(factory, PromptCompareSeed.Run(d.PromptV2, d.Set, d.Apps, PromptCompareSeed.V2Scores,
            DateTime.UtcNow, DateTime.UtcNow, model: model, temperature: temperature, keywordWeight: keywordWeight,
            aiWeight: aiWeight, threshold: threshold));

        var c = await GetCompareAsync(client, $"/api/prompt-tests/compare?baseRunId={d.RunV1}&candidateRunId={other}");

        Assert.False(c.IsFairComparison);
        Assert.Equal("NotComparable", c.Verdict);
        Assert.Contains(c.Warnings, w => w.Contains(expected));
        Assert.StartsWith("Chưa thể so sánh", c.Summary);
    }

    [Fact]
    public async Task Different_evaluation_set_is_not_a_fair_comparison()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var d = await SeedAsync(factory);
        var other = await WithDbAsync(factory, async db =>
        {
            var set = new EvaluationSet { Name = "Bộ CV khác", CreatedAt = DateTime.UtcNow };
            db.EvaluationSets.Add(set);
            await db.SaveChangesAsync();
            for (var i = 0; i < d.Apps.Length; i++) db.HrLabels.Add(PromptCompareSeed.Label(set.EvaluationSetId, d.Apps[i], PromptCompareSeed.Hr[i]));
            var run = PromptCompareSeed.Run(d.PromptV2, set.EvaluationSetId, d.Apps, PromptCompareSeed.V2Scores, DateTime.UtcNow, DateTime.UtcNow);
            db.EvaluationRuns.Add(run);
            await db.SaveChangesAsync();
            return run.RunId;
        });

        var c = await GetCompareAsync(client, $"/api/prompt-tests/compare?baseRunId={d.RunV1}&candidateRunId={other}");

        Assert.False(c.IsFairComparison);
        Assert.Equal("NotComparable", c.Verdict);
        Assert.Contains(c.Warnings, w => w.Contains("Khác bộ CV đánh giá"));
        Assert.NotEqual(c.Base.EvaluationSetId, c.Candidate.EvaluationSetId);
        Assert.Equal(10, c.Items.Count);
    }

    [Fact]
    public async Task Same_set_and_settings_but_different_application_lists_is_not_fair_and_names_the_cvs()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var d = await SeedAsync(factory);
        // HR gán thêm nhãn cho hồ sơ mới sau lượt v1; lượt mới của v2 có 10 CV nhưng thiếu Ngô Gia Khánh
        var other = await WithDbAsync(factory, async db =>
        {
            var jobId = await db.Jobs.OrderBy(j => j.JobId).Select(j => j.JobId).Skip(4).FirstAsync();
            var extra = PromptCompareSeed.NewApplication(jobId, "Dương Mỹ Linh");
            db.Applications.Add(extra);
            await db.SaveChangesAsync();
            db.HrLabels.Add(PromptCompareSeed.Label(d.Set, extra.ApplicationId, true));
            var apps = d.Apps.Take(9).Append(extra.ApplicationId).ToArray();
            var run = PromptCompareSeed.Run(d.PromptV2, d.Set, apps, PromptCompareSeed.V2Scores, DateTime.UtcNow, DateTime.UtcNow);
            db.EvaluationRuns.Add(run);
            await db.SaveChangesAsync();
            return run.RunId;
        });

        var c = await GetCompareAsync(client, $"/api/prompt-tests/compare?baseRunId={d.RunV1}&candidateRunId={other}");

        Assert.False(c.IsFairComparison);
        Assert.Equal("NotComparable", c.Verdict);
        Assert.Equal(9, c.Items.Count);
        Assert.DoesNotContain(c.Items, i => i.ApplicationId == d.Apps[9]);
        var warning = Assert.Single(c.Warnings);
        Assert.Contains($"Ngô Gia Khánh (hồ sơ #{d.Apps[9]})", warning);
        Assert.Contains("Dương Mỹ Linh (hồ sơ #", warning);
        Assert.Contains("9 CV chung", warning);
        // Tóm tắt hai bên tính trên 9 CV chung
        Assert.Equal((6, 7), (c.Base.CorrectCount, c.Candidate.CorrectCount));
    }

    [Fact]
    public async Task Evaluations_saved_in_a_different_order_are_matched_by_application_id()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var d = await SeedAsync(factory);
        var reversedApps = d.Apps.AsEnumerable().Reverse().ToArray();
        var reversedScores = PromptCompareSeed.V2Scores.AsEnumerable().Reverse().ToArray();
        var other = await AddRunAsync(factory, PromptCompareSeed.Run(d.PromptV2, d.Set, reversedApps, reversedScores,
            DateTime.UtcNow, DateTime.UtcNow));

        var c = await GetCompareAsync(client, $"/api/prompt-tests/compare?baseRunId={d.RunV1}&candidateRunId={other}");

        Assert.Equal(Summary, c.Summary);
        Assert.Equal((1, 0), (c.FixedCount, c.BrokenCount));
        Assert.Equal(d.Apps[8], c.Items[0].ApplicationId);
        Assert.All(c.Items, i => Assert.Equal(i.CandidateScore, PromptCompareSeed.V2Scores[Array.IndexOf(d.Apps, i.ApplicationId)]));
    }

    [Fact]
    public async Task Removed_hr_label_is_excluded_from_both_runs_and_comparison_stays_fair()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var d = await SeedAsync(factory);
        await WithDbAsync(factory, async db =>
        {
            db.HrLabels.Remove(await db.HrLabels.SingleAsync(h => h.EvaluationSetId == d.Set && h.ApplicationId == d.Apps[1]));
            return await db.SaveChangesAsync();
        });

        var c = await GetCompareAsync(client, Versions(d));

        Assert.True(c.IsFairComparison);
        Assert.Equal("Better", c.Verdict);
        Assert.Equal(9, c.Items.Count);
        Assert.Equal((77.78m, 88.89m, 11.11m), (c.Base.AccuracyPercent!.Value, c.Candidate.AccuracyPercent!.Value, c.AccuracyDelta!.Value));
        Assert.Equal((0, 0), (c.Base.MissedCount, c.Candidate.MissedCount));
    }

    [Fact]
    public async Task Keyword_only_and_unscored_cvs_are_warned_and_never_counted_as_fixed_or_broken()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var d = await SeedAsync(factory);
        await WithDbAsync(factory, async db =>
        {
            var evaluations = await db.Evaluations.Where(e => e.RunId == d.RunV2).ToListAsync();
            var keyword = evaluations.Single(e => e.ApplicationId == d.Apps[0]);
            keyword.ScoreSource = "KeywordOnly"; keyword.AiScore = null;
            keyword.ErrorMessage = "Gemini vượt hạn mức sau 3 lần thử lại; điểm chỉ tính theo từ khóa.";
            var failed = evaluations.Single(e => e.ApplicationId == d.Apps[2]);
            failed.Status = "Failed"; failed.PredictedPass = null; failed.FinalScore = null; failed.ScoreSource = null;
            failed.ErrorMessage = "CV chưa trích xuất được nội dung (ExtractionStatus = Failed).";
            return await db.SaveChangesAsync();
        });

        var c = await GetCompareAsync(client, Versions(d));

        // v2: đúng 7/10 (hồ sơ lỗi tính là sai) = 70% → bằng v1, nhưng #9 được sửa đúng: kết luận phụ thuộc CV lỗi
        Assert.Equal(70m, c.Candidate.AccuracyPercent);
        Assert.Equal(0m, c.AccuracyDelta);
        Assert.Equal((1, 0, 1), (c.FixedCount, c.BrokenCount, c.UnscoredCount));
        Assert.True(c.IsFairComparison);
        Assert.Equal("NotComparable", c.Verdict);
        Assert.Contains(c.Warnings, w => w.Contains("1 CV chỉ được chấm bằng từ khóa"));
        Assert.Contains(c.Warnings, w => w.Contains("1 CV không chấm được"));
        Assert.StartsWith("Chưa thể kết luận", c.Summary);
        var unscored = c.Items.Single(i => i.ApplicationId == d.Apps[2]);
        Assert.Equal("Unscored", unscored.Change);
        Assert.Null(unscored.CandidatePredictedPass);
        Assert.Null(unscored.CandidateScore);
        Assert.Null(unscored.CandidateIsMatch);
        Assert.Null(unscored.ScoreDelta);
        Assert.True(unscored.BaseIsMatch);
        Assert.Equal("Fixed", c.Items[0].Change);
        Assert.Equal("Unscored", c.Items[1].Change);
    }

    [Fact]
    public async Task Compare_is_read_only_and_does_not_touch_runs_labels_or_prompts()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var d = await SeedAsync(factory);
        await WithDbAsync(factory, async db =>
        {
            (await db.EvaluationRuns.SingleAsync(r => r.RunId == d.RunV1)).AccuracyPercent = 12.34m;   // cache cố ý lệch
            return await db.SaveChangesAsync();
        });
        var snapshot = () => WithDbAsync(factory, async db => JsonSerializer.Serialize(new
        {
            Runs = await db.EvaluationRuns.AsNoTracking().OrderBy(r => r.RunId)
                .Select(r => new { r.RunId, r.Status, r.AccuracyPercent, r.ProcessedItems, r.CompletedAt }).ToListAsync(),
            Evaluations = await db.Evaluations.AsNoTracking().OrderBy(e => e.EvaluationId)
                .Select(e => new { e.EvaluationId, e.Status, e.FinalScore, e.PredictedPass, e.ScoreSource, e.AiRawResponse }).ToListAsync(),
            Labels = await db.HrLabels.AsNoTracking().OrderBy(h => h.ApplicationId).Select(h => new { h.ApplicationId, h.IsPass, h.Note }).ToListAsync(),
            Prompts = await db.PromptVersions.AsNoTracking().OrderBy(p => p.PromptVersionId).Select(p => new { p.PromptVersionId, p.IsActive }).ToListAsync()
        }));
        var before = await snapshot();

        await GetCompareAsync(client, Versions(d));
        await GetCompareAsync(client, $"/api/prompt-tests/compare?baseRunId={d.RunV1}&candidateRunId={d.RunV2}");

        Assert.Equal(before, await snapshot());
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Compare_endpoints_keep_existing_access_boundary(string environment)
    {
        using var factory = Factory(new() { ["CvUpload:Endpoint:Enabled"] = "false" }, environment);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/prompt-tests/compare?baseRunId=1&candidateRunId=2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/prompts/compare?baseVersionId=1&candidateVersionId=2&evaluationSetId=1")).StatusCode);
    }
}
