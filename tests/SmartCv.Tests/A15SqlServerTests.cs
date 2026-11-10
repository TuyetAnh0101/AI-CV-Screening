using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartCv.Api.Background;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Ai;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Gemini;
using SmartCv.Infrastructure.Scoring;

namespace SmartCv.Tests;

/// <summary>
/// Optional A15 SQL checks. SMARTCV_A15_TEST_SQL_CONNECTION must designate a
/// SmartCv_A15_Test_ database. Each test uses a new generated database and deletes
/// only that database. No real CV file, demo data or Gemini call is used.
/// </summary>
public class A15SqlServerTests
{
    [A15SqlFact]
    public async Task Sets_labels_candidates_and_template_use_real_relational_queries()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        var seed = await env.SeedAsync(labels: false);
        using var created = await env.Client.PostAsJsonAsync("/api/evaluation-sets", new CreateEvaluationSetRequest
        {
            Name = "Bộ CV tiếng Việt", Description = "Nhãn của phòng tuyển dụng"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var set = (await created.Content.ReadFromJsonAsync<EvaluationSetDto>())!;
        Assert.Equal("Bộ CV tiếng Việt", set.Name);
        Assert.Equal(HttpStatusCode.Conflict, (await env.Client.PostAsJsonAsync("/api/evaluation-sets",
            new CreateEvaluationSetRequest { Name = set.Name })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await env.Client.PostAsJsonAsync("/api/evaluation-sets",
            new CreateEvaluationSetRequest { Name = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await env.Client.GetAsync("/api/evaluation-sets/2147483647")).StatusCode);

        using var saved = await env.Client.PutAsJsonAsync($"/api/evaluation-sets/{set.EvaluationSetId}/labels", Labels(seed));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var counts = (await saved.Content.ReadFromJsonAsync<SaveHrLabelsResult>())!;
        Assert.Equal((2, 0, 0, 2, 1), (counts.Added, counts.Updated, counts.Unchanged, counts.LabelCount, counts.PassCount));
        var candidates = (await env.Client.GetFromJsonAsync<List<LabelCandidateDto>>(
            $"/api/evaluation-sets/{set.EvaluationSetId}/applications?jobId={seed.JobId}"))!;
        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, a => a.ApplicationId == seed.PassApplicationId && a.CurrentLabel == true);
        Assert.Contains(candidates, a => a.ApplicationId == seed.FailApplicationId && a.CurrentLabel == false);
        var detail = (await env.Client.GetFromJsonAsync<EvaluationSetDetailDto>($"/api/evaluation-sets/{set.EvaluationSetId}"))!;
        Assert.Equal(2, detail.LabelCount);
        Assert.All(detail.Labels, l => Assert.Equal("HR kiểm thử", l.LabeledBy));
        Assert.All(detail.Labels, l => Assert.Equal(DateTimeKind.Utc, l.LabeledAt.Kind));
        var list = (await env.Client.GetFromJsonAsync<List<EvaluationSetDto>>("/api/evaluation-sets"))!;
        Assert.Equal(set.EvaluationSetId, list[0].EvaluationSetId);
        using var template = await env.Client.GetAsync($"/api/evaluation-sets/{set.EvaluationSetId}/labels/template");
        Assert.Equal(HttpStatusCode.OK, template.StatusCode);
        var bytes = await template.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        var csv = Encoding.UTF8.GetString(bytes);
        Assert.Contains("ApplicationId;TenFile;UngVien;ViTri;KetQua;GhiChu", csv);
        Assert.Contains("cv-dat.pdf", csv);
        Assert.DoesNotContain("cv-loi.pdf", csv);
        Assert.Equal(HttpStatusCode.NoContent, (await env.Client.DeleteAsync(
            $"/api/evaluation-sets/{set.EvaluationSetId}/labels/{seed.PassApplicationId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await env.Client.DeleteAsync(
            $"/api/evaluation-sets/{set.EvaluationSetId}/labels/{seed.PassApplicationId}")).StatusCode);
        await using var db = env.Context();
        Assert.Equal(3, await db.Applications.CountAsync());
        Assert.Equal(3, await db.Cvs.CountAsync());
    }

    [A15SqlFact]
    public async Task Csv_import_is_utf8_atomic_and_rejects_ambiguous_file_names()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        var seed = await env.SeedAsync(labels: false);
        var csv = $"\uFEFFMã hồ sơ;Kết quả;Ghi chú\r\n{seed.PassApplicationId};Đạt;Nền tảng tốt\r\n{seed.FailApplicationId};Không đạt;Cần bổ sung\r\n";
        using var imported = await env.Client.PostAsync($"/api/evaluation-sets/{seed.SetId}/labels/import", Csv(csv));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.Equal(2, (await imported.Content.ReadFromJsonAsync<SaveHrLabelsResult>())!.Added);
        var invalid = $"ApplicationId,KetQua,GhiChu\n{seed.PassApplicationId},Không đạt,Không được lưu\n{seed.FailApplicationId},Có lẽ,lỗi\n";
        using var rejected = await env.Client.PostAsync($"/api/evaluation-sets/{seed.SetId}/labels/import", Csv(invalid));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("3", await rejected.Content.ReadAsStringAsync());
        await using (var db = env.Context())
        {
            Assert.True((await db.HrLabels.SingleAsync(h => h.ApplicationId == seed.PassApplicationId)).IsPass);
            var application = await db.Applications.SingleAsync(a => a.ApplicationId == seed.PassApplicationId);
            var otherJob = await db.Jobs.Where(j => j.JobId != seed.JobId).Select(j => j.JobId).FirstAsync();
            db.Applications.Add(new Application { CvId = application.CvId, JobId = otherJob, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        using var ambiguous = await env.Client.PostAsync($"/api/evaluation-sets/{seed.SetId}/labels/import",
            Csv("TenFile;KetQua\ncv-dat.pdf;Đạt\n"));
        Assert.Equal(HttpStatusCode.BadRequest, ambiguous.StatusCode);
        await using var check = env.Context();
        Assert.Equal(2, await check.HrLabels.CountAsync());
    }

    [A15SqlFact]
    public async Task Label_validation_happens_before_any_rows_are_written()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        var seed = await env.SeedAsync(labels: false);
        var request = Labels(seed);
        request.Labels.Add(new HrLabelInput { ApplicationId = int.MaxValue, IsPass = true });
        using var missing = await env.Client.PutAsJsonAsync($"/api/evaluation-sets/{seed.SetId}/labels", request);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        request.Labels.RemoveAt(2);
        request.Labels.Add(new HrLabelInput { ApplicationId = seed.PassApplicationId, IsPass = false });
        using var duplicate = await env.Client.PutAsJsonAsync($"/api/evaluation-sets/{seed.SetId}/labels", request);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        await using var db = env.Context();
        Assert.Empty(await db.HrLabels.ToListAsync());
    }

    [A15SqlFact]
    public async Task Changed_or_deleted_labels_recalculate_accuracy_and_keep_empty_run_accuracy_null()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        var seed = await env.SeedAsync();
        int runId;
        await using (var db = env.Context())
        {
            var run = new EvaluationRun
            {
                PromptVersionId = seed.PromptId, EvaluationSetId = seed.SetId, RunType = "PromptTest",
                Status = "Completed", ModelName = "saved-model", KeywordWeight = .3m, AiWeight = .7m,
                PassThreshold = 70, TotalItems = 2, ProcessedItems = 2, AccuracyPercent = 80,
                CreatedAt = DateTime.UtcNow.AddMinutes(-1), CompletedAt = DateTime.UtcNow
            };
            db.Add(run);
            await db.SaveChangesAsync();
            runId = run.RunId;
        }
        var labels = Labels(seed);
        labels.Labels[0].IsPass = false;
        using var changed = await env.Client.PutAsJsonAsync($"/api/evaluation-sets/{seed.SetId}/labels", labels);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var result = await JsonAsync(changed);
        Assert.Contains(runId, result.GetProperty("recalculatedRunIds").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Empty(result.GetProperty("invalidatedRunIds").EnumerateArray());
        var runs = await env.Client.GetFromJsonAsync<JsonElement>($"/api/prompt-tests?evaluationSetId={seed.SetId}");
        Assert.Equal(JsonValueKind.Null, runs[0].GetProperty("accuracyPercent").ValueKind);
        Assert.Equal("NotCalculated", runs[0].GetProperty("accuracyStatus").GetString());
        Assert.Equal(0, runs[0].GetProperty("missedCount").GetInt32());
        Assert.Equal(0, runs[0].GetProperty("totalLabeledCount").GetInt32());
        await using (var db = env.Context())
        {
            var run = await db.EvaluationRuns.SingleAsync(r => r.RunId == runId);
            Assert.Null(run.AccuracyPercent);
            run.AccuracyPercent = 75; // Simulate a historically stored value supplied by another module.
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NoContent, (await env.Client.DeleteAsync(
            $"/api/evaluation-sets/{seed.SetId}/labels/{seed.FailApplicationId}")).StatusCode);
        await using var check = env.Context();
        Assert.Null((await check.EvaluationRuns.SingleAsync()).AccuracyPercent);
    }

    [A15SqlFact]
    public async Task Prompt_test_snapshots_configuration_and_existing_processor_returns_real_progress()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        var seed = await env.SeedAsync();
        using var response = await env.Client.PostAsJsonAsync("/api/prompt-tests", Start(seed));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await JsonAsync(response);
        var runId = json.GetProperty("runId").GetInt32();
        Assert.Equal("Queued", json.GetProperty("status").GetString());
        Assert.Equal(2, json.GetProperty("totalItems").GetInt32());
        await using (var db = env.Context())
        {
            var run = await db.EvaluationRuns.Include(r => r.Evaluations).SingleAsync(r => r.RunId == runId);
            Assert.Equal((seed.PromptId, seed.SetId, "PromptTest", "saved-a15-model", .4m, .6m, .35m, 80m),
                (run.PromptVersionId, run.EvaluationSetId!.Value, run.RunType, run.ModelName,
                    run.KeywordWeight, run.AiWeight, run.Temperature, run.PassThreshold));
            Assert.Equal(2, run.Evaluations.Count);
            Assert.All(run.Evaluations, e => Assert.Equal("Pending", e.Status));
            var scoring = Options.Create(new ScoringOptions());
            var fakeAi = new FakeAiEvaluator(scoring, Options.Create(new GeminiOptions { FakeDelayMs = 0 }));
            var processor = new RunScoringProcessor(db, fakeAi, scoring, NullLogger<RunScoringProcessor>.Instance);
            await processor.ProcessRunAsync(runId, default);
        }
        var detail = await env.Client.GetFromJsonAsync<JsonElement>($"/api/prompt-tests/{runId}");
        Assert.Equal("Completed", detail.GetProperty("status").GetString());
        Assert.Equal(2, detail.GetProperty("processedItems").GetInt32());
        Assert.Equal(100m, detail.GetProperty("accuracyPercent").GetDecimal());
        Assert.Equal(2, detail.GetProperty("correctCount").GetInt32());
        Assert.Equal(2, detail.GetProperty("items").GetArrayLength());
        foreach (var item in detail.GetProperty("items").EnumerateArray())
        {
            Assert.Equal("Completed", item.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Number, item.GetProperty("finalScore").ValueKind);
            Assert.True(item.GetProperty("isMatch").GetBoolean());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("mismatchType").ValueKind);
        }
        await using var check = env.Context();
        Assert.True((await check.PromptVersions.SingleAsync(p => p.PromptVersionId == seed.ActivePromptId)).IsActive);
        Assert.False((await check.PromptVersions.SingleAsync(p => p.PromptVersionId == seed.PromptId)).IsActive);
        Assert.Equal(100m, (await check.EvaluationRuns.SingleAsync()).AccuracyPercent);
    }

    [A15SqlFact]
    public async Task Concurrent_starts_create_one_run_and_return_existing_run_in_conflict()
    {
        await using var env = await SqlEnvironment.CreateAsync(new StartBarrierInterceptor());
        var seed = await env.SeedAsync();
        var responses = await Task.WhenAll(
            env.Client.PostAsJsonAsync("/api/prompt-tests", Start(seed)),
            env.Client.PostAsJsonAsync("/api/prompt-tests", Start(seed)));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            var conflict = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
            var body = await conflict.Content.ReadAsStringAsync();
            await using var db = env.Context();
            var run = await db.EvaluationRuns.SingleAsync();
            Assert.Contains(run.RunId.ToString(), body);
            Assert.Equal(2, await db.Evaluations.CountAsync());
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [A15SqlFact]
    public async Task Failure_after_run_save_rolls_back_run_and_evaluations_together()
    {
        var failure = new RunSaveFault();
        await using var env = await SqlEnvironment.CreateAsync(saveInterceptor: failure);
        var seed = await env.SeedAsync();
        failure.Armed = true;
        using var response = await env.Client.PostAsJsonAsync("/api/prompt-tests", Start(seed));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(failure.Triggered);
        await using var db = env.Context();
        Assert.Empty(await db.EvaluationRuns.ToListAsync());
        Assert.Empty(await db.Evaluations.ToListAsync());
        Assert.Equal(2, await db.HrLabels.CountAsync());
    }

    private static StartPromptTestRequest Start(Seed seed) => new() { PromptVersionId = seed.PromptId, EvaluationSetId = seed.SetId };
    private static SaveHrLabelsRequest Labels(Seed seed) => new()
    {
        LabeledBy = "HR kiểm thử", Labels =
        [
            new HrLabelInput { ApplicationId = seed.PassApplicationId, IsPass = true, Note = "Có bằng chứng" },
            new HrLabelInput { ApplicationId = seed.FailApplicationId, IsPass = false }
        ]
    };
    private static MultipartFormDataContent Csv(string text)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "file", "nhan-hr.csv");
        form.Add(new StringContent("HR kiểm thử", Encoding.UTF8), "labeledBy");
        return form;
    }
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
    private sealed record Seed(int SetId, int PromptId, int ActivePromptId, int PassApplicationId, int FailApplicationId, int JobId);

    private sealed class StartBarrierInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDLOCK", StringComparison.OrdinalIgnoreCase))
            {
                var arrived = Interlocked.Increment(ref arrivals);
                if (arrived == 2) ready.TrySetResult();
                if (arrived <= 2) await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }
    private sealed class RunSaveFault : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Triggered { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<EvaluationRun>().Any())
            {
                Triggered = true;
                throw new InvalidOperationException("Synthetic failure after SQL save and before transaction commit.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SqlEnvironment : IAsyncDisposable
    {
        private readonly string connection;
        private readonly WebApplicationFactory<Program> factory;
        public HttpClient Client { get; }
        private SqlEnvironment(string connection, WebApplicationFactory<Program> factory)
        {
            this.connection = connection;
            this.factory = factory;
            Client = factory.CreateClient();
        }
        public static async Task<SqlEnvironment> CreateAsync(DbCommandInterceptor? interceptor = null,
            SaveChangesInterceptor? saveInterceptor = null)
        {
            var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SMARTCV_A15_TEST_SQL_CONNECTION"));
            if (!builder.InitialCatalog.StartsWith("SmartCv_A15_Test_", StringComparison.Ordinal)
                || !string.IsNullOrEmpty(builder.AttachDBFilename))
                throw new InvalidOperationException("SQL tests require an explicitly designated SmartCv_A15_Test_ database.");
            builder.InitialCatalog = "SmartCv_A15_Test_" + Guid.NewGuid().ToString("N");
            builder.Pooling = false;
            builder.ConnectTimeout = 10;
            var connection = builder.ConnectionString;
            try
            {
                await using (var setup = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options))
                    await setup.Database.EnsureCreatedAsync();
                var masterBuilder = new SqlConnectionStringBuilder(connection) { InitialCatalog = "master" };
                await using (var master = new SqlConnection(masterBuilder.ConnectionString))
                {
                    await master.OpenAsync();
                    await using var command = master.CreateCommand();
                    // Only this generated database inherits a multi-user setting. The server's model is unchanged.
                    command.CommandText = $"ALTER DATABASE [{builder.InitialCatalog}] SET MULTI_USER";
                    await command.ExecuteNonQueryAsync();
                }
                var factory = TestSupport.ApiFactory(settings: new()
                {
                    ["Scoring:WorkerEnabled"] = "false", ["Gemini:UseFake"] = "true", ["Gemini:FakeDelayMs"] = "0",
                    ["Scoring:ModelName"] = "saved-a15-model", ["Scoring:Temperature"] = "0.35",
                    ["Scoring:KeywordWeight"] = "0.4", ["Scoring:AiWeight"] = "0.6", ["Scoring:PassThreshold"] = "80"
                }).WithWebHostBuilder(host =>
                {
                    host.ConfigureLogging(logging => logging.ClearProviders());
                    host.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<DbContextOptions<SmartCvDbContext>>();
                        services.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                        services.AddDbContext<SmartCvDbContext>(options =>
                        {
                            options.UseSqlServer(connection);
                            if (interceptor is not null) options.AddInterceptors(interceptor);
                            if (saveInterceptor is not null) options.AddInterceptors(saveInterceptor);
                        });
                        foreach (var descriptor in services.Where(s => s.ImplementationType == typeof(RunScoringWorker)
                            || s.ImplementationType == typeof(CvExtractionWorker)).ToList()) services.Remove(descriptor);
                    });
                });
                return new SqlEnvironment(connection, factory);
            }
            catch
            {
                await using var cleanup = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options);
                await cleanup.Database.EnsureDeletedAsync();
                throw;
            }
        }
        public SmartCvDbContext Context() => new(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options);
        public async Task<Seed> SeedAsync(bool labels = true)
        {
            await using var db = Context();
            var job = await db.Jobs.Include(j => j.Skills).OrderBy(j => j.JobId).FirstAsync();
            var otherJobId = await db.Jobs.Where(j => j.JobId != job.JobId).Select(j => j.JobId).FirstAsync();
            var pass = new Application { JobId = job.JobId, Cv = Cv("cv-dat.pdf", string.Join(". ", job.Skills.Select(s => s.DisplayName))) };
            var fail = new Application { JobId = job.JobId, Cv = Cv("cv-khong-dat.pdf", "Ứng viên chưa có kỹ năng công nghệ.") };
            var broken = new Application { JobId = otherJobId, Cv = Cv("cv-loi.pdf", null) };
            broken.Cv.ExtractionStatus = "Failed";
            var set = new EvaluationSet { Name = "Bộ A15 tổng hợp", CreatedAt = DateTime.UtcNow.AddDays(-1) };
            var prompt = new PromptVersion { VersionName = "a15-candidate", SystemPrompt = "Prompt thử chưa kích hoạt", CreatedAt = DateTime.UtcNow };
            var active = new PromptVersion { VersionName = "a15-active", SystemPrompt = "Prompt đang dùng", IsActive = true, CreatedAt = DateTime.UtcNow };
            db.AddRange(pass, fail, broken, set, prompt, active);
            await db.SaveChangesAsync();
            if (labels)
            {
                db.AddRange(
                    new HrLabel { EvaluationSetId = set.EvaluationSetId, ApplicationId = pass.ApplicationId, IsPass = true, Note = "Có bằng chứng", LabeledBy = "HR kiểm thử", LabeledAt = DateTime.UtcNow },
                    new HrLabel { EvaluationSetId = set.EvaluationSetId, ApplicationId = fail.ApplicationId, IsPass = false, LabeledBy = "HR kiểm thử", LabeledAt = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
            return new Seed(set.EvaluationSetId, prompt.PromptVersionId, active.PromptVersionId, pass.ApplicationId, fail.ApplicationId, job.JobId);
        }
        private static Cv Cv(string file, string? text)
        {
            var id = Guid.NewGuid();
            return new Cv
            {
                CvId = id, OriginalFileName = file, StoredPath = "synthetic/" + id.ToString("N"), ContentType = "application/pdf",
                FileSizeBytes = 128, FileHash = id.ToString("N").PadRight(64, '0').ToUpperInvariant(),
                ExtractionStatus = "Completed", ExtractionMethod = "PdfText", ExtractedText = text, UploadedAt = DateTime.UtcNow
            };
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
            await using var cleanup = Context();
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}

public sealed class A15SqlFactAttribute : FactAttribute
{
    public A15SqlFactAttribute()
    {
        var value = Environment.GetEnvironmentVariable("SMARTCV_A15_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(value))
        {
            Skip = "NOT RUN: set SMARTCV_A15_TEST_SQL_CONNECTION for a disposable SmartCv_A15_Test_ database.";
            return;
        }
        try
        {
            var builder = new SqlConnectionStringBuilder(value);
            if (!builder.InitialCatalog.StartsWith("SmartCv_A15_Test_", StringComparison.Ordinal)
                || !string.IsNullOrEmpty(builder.AttachDBFilename))
                Skip = "NOT RUN: SQL connection must designate a SmartCv_A15_Test_ database without AttachDBFilename.";
        }
        catch (ArgumentException) { Skip = "NOT RUN: SQL test connection setting is invalid."; }
    }
}
