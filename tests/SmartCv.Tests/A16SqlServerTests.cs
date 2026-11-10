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
using SmartCv.Core.Ai;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Scoring;

namespace SmartCv.Tests;

/// <summary>Real SQL tests use only generated databases and synthetic CV text; never call Gemini.</summary>
public class A16SqlServerTests
{
    [A16SqlFact]
    public async Task Synthetic_demo_matches_api_stored_values_and_read_only_sql_check()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        var data = await env.SeedDemoAsync();
        await AssertRunAsync(env, data.V1, 10, 7, 1, 2, 0, 70m);
        await AssertRunAsync(env, data.V2, 10, 8, 1, 1, 0, 80m);
        var sql = await SqlChecksAsync(env);
        Assert.Equal((10, 7, 1, 2, 0, (decimal?)70m, (decimal?)70m), sql[data.V1]);
        Assert.Equal((10, 8, 1, 1, 0, (decimal?)80m, (decimal?)80m), sql[data.V2]);
        var detail = await env.Client.GetFromJsonAsync<JsonElement>($"/api/prompt-tests/{data.V1}");
        var items = detail.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items, i => i.GetProperty("mismatchType").GetString() == "Missed");
        Assert.Equal(2, items.Count(i => i.GetProperty("mismatchType").GetString() == "WrongPick"));
        Assert.Equal(7, items.Count(i => i.GetProperty("isMatch").ValueKind == JsonValueKind.True));
    }

    [A16SqlFact]
    public async Task Hr_label_flip_and_restore_recalculate_only_the_selected_set()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        var data = await env.SeedDemoAsync();
        int otherRun;
        await using (var db = env.Context())
        {
            var other = new EvaluationSet { Name = "Other synthetic set" };
            other.HrLabels.Add(Label(data.Apps[0], false));
            other.HrLabels.Add(Label(data.Apps[1], true));
            var run = NewRun(data.PromptV1, other, [data.Apps[0], data.Apps[1]], [true, true]);
            run.AccuracyPercent = 50m;
            db.Add(run);
            await db.SaveChangesAsync();
            otherRun = run.RunId;
        }
        using var changed = await env.Client.PutAsJsonAsync($"/api/evaluation-sets/{data.Set}/labels",
            Update(data.Apps[1], false));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var result = await JsonAsync(changed);
        Assert.Equal(new[] { data.V1, data.V2 }.Order(),
            result.GetProperty("recalculatedRunIds").EnumerateArray().Select(x => x.GetInt32()).Order());
        await using (var stored = env.Context())
        {
            Assert.Equal(80m, (await stored.EvaluationRuns.SingleAsync(r => r.RunId == data.V1)).AccuracyPercent);
            Assert.Equal(90m, (await stored.EvaluationRuns.SingleAsync(r => r.RunId == data.V2)).AccuracyPercent);
        }
        await AssertRunAsync(env, data.V1, 10, 8, 0, 2, 0, 80m);
        await AssertRunAsync(env, data.V2, 10, 9, 0, 1, 0, 90m);
        await using (var check = env.Context())
            Assert.Equal(50m, (await check.EvaluationRuns.SingleAsync(r => r.RunId == otherRun)).AccuracyPercent);
        using var restored = await env.Client.PutAsJsonAsync($"/api/evaluation-sets/{data.Set}/labels",
            Update(data.Apps[1], true));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        await using (var stored = env.Context())
        {
            Assert.Equal(70m, (await stored.EvaluationRuns.SingleAsync(r => r.RunId == data.V1)).AccuracyPercent);
            Assert.Equal(80m, (await stored.EvaluationRuns.SingleAsync(r => r.RunId == data.V2)).AccuracyPercent);
        }
        await AssertRunAsync(env, data.V1, 10, 7, 1, 2, 0, 70m);
        await AssertRunAsync(env, data.V2, 10, 8, 1, 1, 0, 80m);
    }

    [A16SqlFact]
    public async Task Csv_change_and_label_deletion_update_accuracy_but_keep_old_evaluations_visible()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        var data = await env.SeedDemoAsync();
        using var csv = new MultipartFormDataContent();
        csv.Add(new ByteArrayContent(Encoding.UTF8.GetBytes($"\uFEFFApplicationId;KetQua\r\n{data.Apps[1]};Không đạt\r\n")), "file", "labels.csv");
        csv.Add(new StringContent("HR synthetic", Encoding.UTF8), "labeledBy");
        using var imported = await env.Client.PostAsync($"/api/evaluation-sets/{data.Set}/labels/import", csv);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        await AssertRunAsync(env, data.V1, 10, 8, 0, 2, 0, 80m);
        Assert.Equal(HttpStatusCode.NoContent, (await env.Client.DeleteAsync(
            $"/api/evaluation-sets/{data.Set}/labels/{data.Apps[1]}")).StatusCode);
        await AssertRunAsync(env, data.V1, 9, 7, 0, 2, 0, 77.78m);
        await AssertRunAsync(env, data.V2, 9, 8, 0, 1, 0, 88.89m);
        var detail = await env.Client.GetFromJsonAsync<JsonElement>($"/api/prompt-tests/{data.V1}");
        Assert.Equal(10, detail.GetProperty("items").GetArrayLength());
        var removed = detail.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("applicationId").GetInt32() == data.Apps[1]);
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("hrIsPass").ValueKind);
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("isMatch").ValueKind);
    }

    [A16SqlFact]
    public async Task Recalculation_failure_rolls_back_hr_change_and_cached_percentages_together()
    {
        var fault = new AccuracySaveFault();
        await using var env = await SqlEnvironment.CreateAsync(saveInterceptor: fault);
        var data = await env.SeedDemoAsync(stored: true);
        fault.Armed = true;
        using var response = await env.Client.PutAsJsonAsync($"/api/evaluation-sets/{data.Set}/labels",
            Update(data.Apps[1], false));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(fault.Triggered);
        await using var db = env.Context();
        Assert.True((await db.HrLabels.SingleAsync(h => h.EvaluationSetId == data.Set && h.ApplicationId == data.Apps[1])).IsPass);
        Assert.Equal(70m, (await db.EvaluationRuns.SingleAsync(r => r.RunId == data.V1)).AccuracyPercent);
        Assert.Equal(80m, (await db.EvaluationRuns.SingleAsync(r => r.RunId == data.V2)).AccuracyPercent);
    }

    [A16SqlFact]
    public async Task Removed_all_labels_produce_null_and_do_not_queue_infinite_recovery()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        var data = await env.SeedDemoAsync(stored: true);
        foreach (var application in data.Apps)
            Assert.Equal(HttpStatusCode.NoContent, (await env.Client.DeleteAsync(
                $"/api/evaluation-sets/{data.Set}/labels/{application}")).StatusCode);
        var noAi = new NeverAi();
        await using (var db = env.Context())
        {
            var processor = Processor(db, noAi);
            var active = await processor.FindActiveRunsAsync(default);
            Assert.DoesNotContain(data.V1, active);
            Assert.DoesNotContain(data.V2, active);
            await processor.ProcessRunAsync(data.V1, default);
            Assert.Null((await db.EvaluationRuns.SingleAsync(r => r.RunId == data.V1)).AccuracyPercent);
        }
        var detail = await env.Client.GetFromJsonAsync<JsonElement>($"/api/prompt-tests/{data.V1}");
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("accuracyPercent").ValueKind);
        Assert.Equal(0, detail.GetProperty("totalLabeledCount").GetInt32());
        Assert.Equal(10, detail.GetProperty("items").GetArrayLength());
        Assert.Equal(0, noAi.Calls);
    }

    [A16SqlFact]
    public async Task Failed_pending_and_null_predictions_remain_in_denominator_and_keyword_only_is_scored()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        var data = await env.SeedDemoAsync();
        await using (var db = env.Context())
        {
            var evaluations = await db.Evaluations.Where(e => e.RunId == data.V1).OrderBy(e => e.ApplicationId).ToListAsync();
            foreach (var evaluation in evaluations.Skip(4)) db.Evaluations.Remove(evaluation);
            evaluations[0].Status = "Completed"; evaluations[0].PredictedPass = true; evaluations[0].ScoreSource = "KeywordOnly";
            evaluations[0].AiScore = null;
            evaluations[1].Status = "Failed"; evaluations[1].PredictedPass = true;
            evaluations[2].Status = "Pending"; evaluations[2].PredictedPass = true;
            evaluations[3].Status = "Completed"; evaluations[3].PredictedPass = null;
            await db.SaveChangesAsync();
        }
        await AssertRunAsync(env, data.V1, 4, 1, 0, 0, 3, 25m);
        var detail = await env.Client.GetFromJsonAsync<JsonElement>($"/api/prompt-tests/{data.V1}");
        Assert.Equal(1, detail.GetProperty("keywordOnlyCount").GetInt32());
        Assert.Equal(3, detail.GetProperty("items").EnumerateArray().Count(i => i.GetProperty("isMatch").ValueKind == JsonValueKind.Null));
    }

    [A16SqlFact]
    public async Task Restart_backfills_completed_run_without_rescoring_and_leaves_screening_unchanged()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        var data = await env.SeedDemoAsync();
        var noAi = new NeverAi();
        int screening;
        await using (var db = env.Context())
        {
            var row = new EvaluationRun
            {
                RunType = "Screening", PromptVersionId = data.PromptV1, Status = "Completed", ModelName = "saved",
                KeywordWeight = .3m, AiWeight = .7m, TotalItems = 1, ProcessedItems = 1, AccuracyPercent = 55m,
                Evaluations = [new Evaluation { ApplicationId = data.Apps[0], Status = "Completed", FinalScore = 83m, PredictedPass = true }]
            };
            db.Add(row);
            await db.SaveChangesAsync();
            screening = row.RunId;
        }
        await using (var db = env.Context())
        {
            var processor = Processor(db, noAi);
            var active = await processor.FindActiveRunsAsync(default);
            Assert.Contains(data.V1, active);
            Assert.Contains(data.V2, active);
            Assert.DoesNotContain(screening, active);
            await processor.ProcessRunAsync(data.V1, default);
        }
        await using (var restarted = env.Context())
        {
            var processor = Processor(restarted, noAi);
            Assert.DoesNotContain(data.V1, await processor.FindActiveRunsAsync(default));
            await processor.ProcessRunAsync(data.V1, default);
            await processor.ProcessRunAsync(data.V2, default);
            await processor.ProcessRunAsync(screening, default);
            Assert.Equal(70m, (await restarted.EvaluationRuns.SingleAsync(r => r.RunId == data.V1)).AccuracyPercent);
            Assert.Equal(80m, (await restarted.EvaluationRuns.SingleAsync(r => r.RunId == data.V2)).AccuracyPercent);
            Assert.Equal(55m, (await restarted.EvaluationRuns.SingleAsync(r => r.RunId == screening)).AccuracyPercent);
            Assert.Equal(83m, (await restarted.Evaluations.SingleAsync(e => e.RunId == screening)).FinalScore);
        }
        Assert.Equal(0, noAi.Calls);
    }

    [A16SqlFact]
    public async Task Running_run_does_not_publish_final_accuracy_or_counters()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        var data = await env.SeedDemoAsync(status: "Running");
        await using (var db = env.Context())
        {
            (await db.EvaluationRuns.SingleAsync(r => r.RunId == data.V1)).AccuracyPercent = 99m;
            await db.SaveChangesAsync();
        }
        var detail = await env.Client.GetFromJsonAsync<JsonElement>($"/api/prompt-tests/{data.V1}");
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("accuracyPercent").ValueKind);
        foreach (var key in new[] { "correctCount", "missedCount", "wrongPickCount", "unscoredCount" })
            Assert.Equal(JsonValueKind.Null, detail.GetProperty(key).ValueKind);
    }

    [A16SqlFact]
    public async Task Added_label_is_not_inserted_into_old_runs_and_same_filename_other_job_is_not_joined()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        var data = await env.SeedDemoAsync(stored: true);
        int extraId;
        await using (var db = env.Context())
        {
            var original = await db.Applications.SingleAsync(a => a.ApplicationId == data.Apps[0]);
            var otherJob = await db.Jobs.Where(j => j.JobId != original.JobId).Select(j => j.JobId).FirstAsync();
            var extra = new Application { CvId = original.CvId, JobId = otherJob };
            db.Add(extra);
            await db.SaveChangesAsync();
            extraId = extra.ApplicationId;
        }
        using var response = await env.Client.PutAsJsonAsync($"/api/evaluation-sets/{data.Set}/labels", Update(extraId, true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.NotEmpty(body.GetProperty("warnings").EnumerateArray());
        await AssertRunAsync(env, data.V1, 10, 7, 1, 2, 0, 70m);
        await AssertRunAsync(env, data.V2, 10, 8, 1, 1, 0, 80m);
        await using var check = env.Context();
        Assert.Equal(11, await check.HrLabels.CountAsync(h => h.EvaluationSetId == data.Set));
        Assert.Equal(20, await check.Evaluations.CountAsync());
    }

    [A16SqlFact]
    public async Task Concurrent_hr_update_and_final_evaluation_commit_have_consistent_accuracy()
    {
        var barrier = new WriteBarrier();
        await using var env = await SqlEnvironment.CreateAsync(barrier);
        var data = await env.SeedDemoAsync();
        await using (var setup = env.Context())
        {
            var run = await setup.EvaluationRuns.SingleAsync(r => r.RunId == data.V1);
            run.Status = "Running"; run.ProcessedItems = 9;
            var pending = await setup.Evaluations.SingleAsync(e => e.RunId == data.V1 && e.ApplicationId == data.Apps[1]);
            pending.Status = "Pending"; pending.PredictedPass = null;
            await setup.SaveChangesAsync();
        }
        barrier.Armed = true;
        await using (var workerDb = env.Context())
        {
            var worker = Processor(workerDb, new RejectAi());
            var processing = worker.ProcessRunAsync(data.V1, default);
            var updating = env.Client.PutAsJsonAsync($"/api/evaluation-sets/{data.Set}/labels", Update(data.Apps[1], false));
            await Task.WhenAll(processing, updating);
            using var response = await updating;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        // Verify the committed race outcome BEFORE a GET can repair the accuracy cache.
        await using (var check = env.Context())
        {
            var saved = await check.EvaluationRuns.SingleAsync(r => r.RunId == data.V1);
            Assert.Equal("Completed", saved.Status);
            Assert.Equal(80m, saved.AccuracyPercent);
        }
        var sql = await SqlChecksAsync(env);
        Assert.Equal((decimal?)80m, sql[data.V1].Item6);
        Assert.Equal((decimal?)80m, sql[data.V1].Item7);
        await AssertRunAsync(env, data.V1, 10, 8, 0, 2, 0, 80m);
    }

    private static RunScoringProcessor Processor(SmartCvDbContext db, IAiEvaluator ai) =>
        new(db, ai, Options.Create(new ScoringOptions()), NullLogger<RunScoringProcessor>.Instance);

    private static async Task AssertRunAsync(SqlEnvironment env, int runId, int total, int correct, int missed,
        int wrong, int unscored, decimal accuracy)
    {
        var dto = await env.Client.GetFromJsonAsync<JsonElement>($"/api/prompt-tests/{runId}");
        Assert.Equal(total, dto.GetProperty("totalLabeledCount").GetInt32());
        Assert.Equal(correct, dto.GetProperty("correctCount").GetInt32());
        Assert.Equal(missed, dto.GetProperty("missedCount").GetInt32());
        Assert.Equal(wrong, dto.GetProperty("wrongPickCount").GetInt32());
        Assert.Equal(unscored, dto.GetProperty("unscoredCount").GetInt32());
        Assert.Equal(total, correct + missed + wrong + unscored);
        Assert.Equal(accuracy, dto.GetProperty("accuracyPercent").GetDecimal());
        await using var db = env.Context();
        Assert.Equal(accuracy, (await db.EvaluationRuns.SingleAsync(r => r.RunId == runId)).AccuracyPercent);
    }

    private static async Task<Dictionary<int, (int, int, int, int, int, decimal?, decimal?)>> SqlChecksAsync(SqlEnvironment env)
    {
        await using var db = env.Context();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = await File.ReadAllTextAsync(TestSupport.RepoFile("database/check_a16.sql"));
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new Dictionary<int, (int, int, int, int, int, decimal?, decimal?)>();
        while (await reader.ReadAsync())
            rows[reader.GetInt32(reader.GetOrdinal("RunId"))] = (
                reader.GetInt32(reader.GetOrdinal("Tong")), reader.GetInt32(reader.GetOrdinal("Dung")),
                reader.GetInt32(reader.GetOrdinal("BoSot")), reader.GetInt32(reader.GetOrdinal("ChonNham")),
                reader.GetInt32(reader.GetOrdinal("KhongCham")),
                reader.IsDBNull(reader.GetOrdinal("TyLeTinhLai")) ? null : reader.GetDecimal(reader.GetOrdinal("TyLeTinhLai")),
                reader.IsDBNull(reader.GetOrdinal("TyLeDaLuu")) ? null : reader.GetDecimal(reader.GetOrdinal("TyLeDaLuu")));
        return rows;
    }

    private static SaveHrLabelsRequest Update(int applicationId, bool isPass) => new()
    {
        LabeledBy = "HR synthetic", Labels = [new HrLabelInput { ApplicationId = applicationId, IsPass = isPass }]
    };
    private static HrLabel Label(int applicationId, bool isPass) => new()
    {
        ApplicationId = applicationId, IsPass = isPass, LabeledBy = "HR synthetic", LabeledAt = DateTime.UtcNow
    };
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
    private sealed record Demo(int Set, int PromptV1, int V1, int V2, int[] Apps);
    private static EvaluationRun NewRun(int prompt, EvaluationSet set, int[] applications, bool[] predictions) => new()
    {
        PromptVersionId = prompt, EvaluationSet = set, RunType = "PromptTest", Status = "Completed",
        ModelName = "synthetic-model", KeywordWeight = .3m, AiWeight = .7m, PassThreshold = 70m,
        TotalItems = applications.Length, ProcessedItems = applications.Length,
        CreatedAt = DateTime.UtcNow.AddMinutes(-1), CompletedAt = DateTime.UtcNow,
        Evaluations = applications.Select((id, index) => new Evaluation
        {
            ApplicationId = id, Status = "Completed", FinalScore = predictions[index] ? 90m : 40m,
            AiScore = predictions[index] ? 10m : 99m, PredictedPass = predictions[index], ScoreSource = "Combined",
            Reasoning = "Synthetic saved reasoning", AiRawResponse = "{}", CreatedAt = DateTime.UtcNow
        }).ToList()
    };

    private sealed class NeverAi : IAiEvaluator
    {
        public int Calls { get; private set; }
        public Task<AiEvaluationResult> EvaluateAsync(AiEvaluationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Completed results must not call AI again.");
        }
    }
    private sealed class RejectAi : IAiEvaluator
    {
        public Task<AiEvaluationResult> EvaluateAsync(AiEvaluationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiEvaluationResult
            {
                IsSuccess = true, Score = 0, MatchedSkillCodes = request.Job.Skills.Select(s => s.SkillCode).ToArray(),
                Reasoning = "Synthetic negative prediction", RawResponse = "{\"score\":0}", IsSimulated = true
            });
    }
    private sealed class AccuracySaveFault : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Triggered { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<EvaluationRun>()
                .Any(e => e.Property(r => r.AccuracyPercent).IsModified))
            {
                Triggered = true;
                throw new InvalidOperationException("Synthetic failure before accuracy save.");
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class WriteBarrier : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("UPDLOCK", StringComparison.OrdinalIgnoreCase))
            {
                var arrived = Interlocked.Increment(ref arrivals);
                if (arrived == 2) ready.TrySetResult();
                if (arrived <= 2) await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }

    private sealed class SqlEnvironment : IAsyncDisposable
    {
        private readonly string connection;
        private readonly WebApplicationFactory<Program> factory;
        private readonly IInterceptor[] interceptors;
        public HttpClient Client { get; }
        private SqlEnvironment(string connection, WebApplicationFactory<Program> factory, IInterceptor[] interceptors)
        {
            this.connection = connection; this.factory = factory; this.interceptors = interceptors;
            Client = factory.CreateClient();
        }
        public static async Task<SqlEnvironment> CreateAsync(DbCommandInterceptor? interceptor = null,
            SaveChangesInterceptor? saveInterceptor = null)
        {
            var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SMARTCV_A16_TEST_SQL_CONNECTION"));
            if (!builder.InitialCatalog.StartsWith("SmartCv_A16_Test_", StringComparison.Ordinal)
                || !string.IsNullOrEmpty(builder.AttachDBFilename))
                throw new InvalidOperationException("SQL tests require a designated SmartCv_A16_Test_ database.");
            builder.InitialCatalog = "SmartCv_A16_Test_" + Guid.NewGuid().ToString("N");
            builder.Pooling = false; builder.ConnectTimeout = 10;
            var connection = builder.ConnectionString;
            var interceptors = new IInterceptor?[] { interceptor, saveInterceptor }.OfType<IInterceptor>().ToArray();
            try
            {
                await using (var setup = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options))
                    await setup.Database.EnsureCreatedAsync();
                var masterBuilder = new SqlConnectionStringBuilder(connection) { InitialCatalog = "master" };
                await using (var master = new SqlConnection(masterBuilder.ConnectionString))
                {
                    await master.OpenAsync();
                    await using var command = master.CreateCommand();
                    command.CommandText = $"ALTER DATABASE [{builder.InitialCatalog}] SET MULTI_USER";
                    await command.ExecuteNonQueryAsync();
                }
                var factory = TestSupport.ApiFactory(settings: new()
                {
                    ["Scoring:WorkerEnabled"] = "false", ["Gemini:UseFake"] = "true", ["Gemini:FakeDelayMs"] = "0"
                }).WithWebHostBuilder(host =>
                {
                    host.ConfigureLogging(logging => logging.ClearProviders());
                    host.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<DbContextOptions<SmartCvDbContext>>();
                        services.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                        services.AddDbContext<SmartCvDbContext>(options => options.UseSqlServer(connection).AddInterceptors(interceptors));
                        foreach (var descriptor in services.Where(s => s.ImplementationType == typeof(RunScoringWorker)
                            || s.ImplementationType == typeof(CvExtractionWorker)).ToList()) services.Remove(descriptor);
                    });
                });
                return new(connection, factory, interceptors);
            }
            catch
            {
                await using var cleanup = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options);
                await cleanup.Database.EnsureDeletedAsync();
                throw;
            }
        }
        public SmartCvDbContext Context() => new(new DbContextOptionsBuilder<SmartCvDbContext>()
            .UseSqlServer(connection).AddInterceptors(interceptors).Options);

        public async Task<Demo> SeedDemoAsync(bool stored = false, string status = "Completed")
        {
            await using var db = Context();
            var job = await db.Jobs.Include(j => j.Skills).OrderBy(j => j.JobId).FirstAsync();
            var applications = Enumerable.Range(0, 10).Select(index =>
            {
                var id = Guid.NewGuid();
                return new Application
                {
                    JobId = job.JobId, Cv = new Cv
                    {
                        CvId = id, OriginalFileName = "same-name.pdf", StoredPath = "synthetic/" + id.ToString("N"),
                        ContentType = "application/pdf", FileSizeBytes = 100,
                        FileHash = id.ToString("N").PadRight(64, '0').ToUpperInvariant(),
                        ExtractedText = string.Join(". ", job.Skills.Select(s => s.DisplayName)),
                        ExtractionStatus = "Completed", ExtractionMethod = "PdfText", UploadedAt = DateTime.UtcNow
                    }
                };
            }).ToArray();
            var v1 = new PromptVersion { VersionName = "synthetic-v1", SystemPrompt = "SYNTHETIC ONE" };
            var v2 = new PromptVersion { VersionName = "synthetic-v2", SystemPrompt = "SYNTHETIC TWO", IsActive = true };
            var set = new EvaluationSet { Name = "Synthetic A16 ten CV set", CreatedAt = DateTime.UtcNow };
            db.AddRange(v1, v2, set); db.AddRange(applications);
            await db.SaveChangesAsync();
            var ids = applications.Select(a => a.ApplicationId).ToArray();
            for (var i = 0; i < ids.Length; i++) set.HrLabels.Add(Label(ids[i], i < 5));
            var p1 = Enumerable.Range(0, 10).Select(i => i < 5).ToArray();
            var p2 = p1.ToArray();
            foreach (var index in new[] { 1, 6, 7 }) p1[index] = !p1[index];
            foreach (var index in new[] { 1, 6 }) p2[index] = !p2[index];
            var r1 = NewRun(v1.PromptVersionId, set, ids, p1);
            var r2 = NewRun(v2.PromptVersionId, set, ids, p2);
            r1.Status = status; r2.Status = status;
            if (stored) { r1.AccuracyPercent = 70m; r2.AccuracyPercent = 80m; }
            db.AddRange(r1, r2);
            await db.SaveChangesAsync();
            return new(set.EvaluationSetId, v1.PromptVersionId, r1.RunId, r2.RunId, ids);
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await factory.DisposeAsync();
            await using var cleanup = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}

public sealed class A16SqlFactAttribute : FactAttribute
{
    public A16SqlFactAttribute()
    {
        var value = Environment.GetEnvironmentVariable("SMARTCV_A16_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(value))
        {
            Skip = "NOT RUN: set SMARTCV_A16_TEST_SQL_CONNECTION for a disposable SmartCv_A16_Test_ database.";
            return;
        }
        try
        {
            var builder = new SqlConnectionStringBuilder(value);
            if (!builder.InitialCatalog.StartsWith("SmartCv_A16_Test_", StringComparison.Ordinal)
                || !string.IsNullOrEmpty(builder.AttachDBFilename))
                Skip = "NOT RUN: SQL connection must designate SmartCv_A16_Test_ without AttachDBFilename.";
        }
        catch (ArgumentException) { Skip = "NOT RUN: SQL test connection is invalid."; }
    }
}
