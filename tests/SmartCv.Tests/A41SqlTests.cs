using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SmartCv.Api.Background;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

/// <summary>
/// A41 trên SQL Server thật (opt-in): chạy database/check_a41_khong_tao_dong.sql trước/sau khi gọi ai-preview (nháp, phiên bản,
/// đang dùng), assembled-preview và quota; mọi con số phải giữ nguyên. Catalog MỚI SmartCv_A41_Test_&lt;GUID&gt;, dữ liệu tổng hợp,
/// AI giả (không gọi Gemini). Catalog được GIỮ LẠI (không DROP); tên ghi vào artifacts/a41-tests/retained-sql-databases.txt.
/// Không bao giờ dùng SmartCvDb.
/// </summary>
public class A41SqlTests
{
    [A41SqlFact]
    public async Task Prompt_tools_create_and_change_no_rows_on_sql_server()
    {
        await using var env = await Env.CreateAsync();
        var seed = await env.SeedAsync();
        var before = await env.CountsAsync();

        foreach (var body in new object?[] { null, new { systemPrompt = "Bản nháp A41 trên SQL Server." }, new { promptVersionId = seed.SavedPromptId } })
        {
            using var response = body is null
                ? await env.Http.PostAsync($"/api/applications/{seed.ApplicationId}/ai-preview", null)
                : await env.Http.PostAsJsonAsync($"/api/applications/{seed.ApplicationId}/ai-preview", body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(seed.EvaluationId, json.GetProperty("currentEvaluation").GetProperty("evaluationId").GetInt32());
        }

        using (var assembled = await env.Http.PostAsJsonAsync("/api/prompts/assembled-preview",
                   new { applicationId = seed.ApplicationId, systemPrompt = "Bản nháp A41." }))
        {
            var text = await assembled.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, assembled.StatusCode);
            Assert.Equal("no-store", assembled.Headers.CacheControl?.ToString());   // đi qua khóa đọc dữ liệu CV (A39) trên SQL Server
            Assert.DoesNotContain("lananh.nguyen", text);
        }

        using (var quota = await env.Http.GetAsync("/api/applications/ai-preview/quota"))
            Assert.Equal(HttpStatusCode.OK, quota.StatusCode);

        var after = await env.CountsAsync();
        Assert.Equal(before, after);

        var evidence = TestSupport.RepoFile("artifacts/a41-tests/sql-no-write-evidence.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        await File.AppendAllTextAsync(evidence,
            $"{DateTime.UtcNow:O} {env.Catalog}{Environment.NewLine}  before: {before}{Environment.NewLine}  after:  {after}{Environment.NewLine}");
    }

    private sealed record Seeded(int ApplicationId, int SavedPromptId, int EvaluationId);

    private sealed class Env : IAsyncDisposable
    {
        private readonly string _connection;
        public string Catalog { get; }
        public WebApplicationFactory<Program> Factory { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;

        private Env(string connection, string catalog) { _connection = connection; Catalog = catalog; }

        public SmartCvDbContext Db() => new(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(_connection).Options);

        public static async Task<Env> CreateAsync()
        {
            var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A41SqlFactAttribute.Setting));
            A41SqlFactAttribute.Validate(builder);
            builder.InitialCatalog = A41SqlFactAttribute.Prefix + Guid.NewGuid().ToString("N");
            builder.Pooling = false;
            builder.ConnectTimeout = 10;
            var env = new Env(builder.ConnectionString, builder.InitialCatalog);

            var retained = TestSupport.RepoFile("artifacts/a41-tests/retained-sql-databases.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(retained)!);
            await File.AppendAllTextAsync(retained, builder.InitialCatalog + Environment.NewLine);
            await using (var db = env.Db())
                await db.Database.MigrateAsync();

            env.Factory = TestSupport.ApiFactory(settings: new()
            {
                ["Scoring:WorkerEnabled"] = "false", ["Gemini:UseFake"] = "true", ["Gemini:FakeDelayMs"] = "0", ["Retention:AutoAnonymize"] = "false"
            }).WithWebHostBuilder(b =>
            {
                b.ConfigureLogging(l => l.ClearProviders());
                b.ConfigureTestServices(s =>
                {
                    s.RemoveAll<DbContextOptions<SmartCvDbContext>>();
                    s.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                    s.AddDbContext<SmartCvDbContext>(o => o.UseSqlServer(env._connection));
                    foreach (var d in s.Where(d => d.ImplementationType == typeof(RunScoringWorker) || d.ImplementationType == typeof(RetentionWorker)).ToList())
                        s.Remove(d);
                });
            });
            env.Http = env.Factory.CreateClient();
            return env;
        }

        public async Task<Seeded> SeedAsync()
        {
            await using var db = Db();
            var now = DateTime.UtcNow;
            var saved = new PromptVersion { VersionName = "a41-luu", SystemPrompt = "Prompt đã lưu A41.", CreatedAt = now.AddDays(-2) };
            var active = new PromptVersion { VersionName = "a41-dang-dung", SystemPrompt = "Prompt đang dùng A41.", IsActive = true, CreatedAt = now.AddDays(-1) };
            var cvId = Guid.NewGuid();
            var application = new Application
            {
                JobId = 1, CreatedAt = now,
                Cv = new Cv
                {
                    CvId = cvId, OriginalFileName = "CV_tong_hop_A41.pdf", StoredPath = "synthetic/" + cvId.ToString("N"),
                    ContentType = "application/pdf", FileSizeBytes = 100, FileHash = cvId.ToString("N").PadRight(64, '0').ToUpperInvariant(),
                    ExtractedText = AiPromptAssemblerTests.ClearImageCv, ExtractionMethod = ExtractionMethods.ImageOcr,
                    ExtractionStatus = ExtractionStatuses.Completed, UploadedAt = now,
                    Candidate = new Candidate { FullName = "Nguyễn Thị Lan Anh", CreatedAt = now }
                }
            };
            var set = new EvaluationSet { Name = "Bộ A41 SQL", CreatedAt = now };
            db.AddRange(saved, active, application, set);
            await db.SaveChangesAsync();

            var evaluation = new Evaluation
            {
                ApplicationId = application.ApplicationId, Status = EvaluationStatuses.Completed, KeywordScore = 80m, AiScore = 75m,
                FinalScore = 76.5m, ScoreSource = ScoreSources.Combined, FitLabel = "Phù hợp", PredictedPass = true,
                Reasoning = "Kết quả tổng hợp đã lưu.", AiRawResponse = "{}", CreatedAt = now
            };
            db.EvaluationRuns.Add(new EvaluationRun
            {
                RunType = RunTypes.Screening, PromptVersionId = active.PromptVersionId, ModelName = "gemini-saved", Temperature = 0.2m,
                KeywordWeight = 0.3m, AiWeight = 0.7m, PassThreshold = 70m, Status = RunStatuses.Completed, TotalItems = 1, ProcessedItems = 1,
                CreatedAt = now, CompletedAt = now, Evaluations = [evaluation]
            });
            db.HrLabels.Add(new HrLabel { EvaluationSetId = set.EvaluationSetId, ApplicationId = application.ApplicationId, IsPass = true, LabeledBy = "HR tổng hợp", LabeledAt = now });
            db.ApplicationEvents.Add(new ApplicationEvent
            {
                ApplicationId = application.ApplicationId, FromStage = ApplicationStages.New, ToStage = ApplicationStages.Reviewing, Actor = "HR tổng hợp", CreatedAt = now
            });
            await db.SaveChangesAsync();
            db.EvaluationSkills.Add(new EvaluationSkill { EvaluationId = evaluation.EvaluationId, JobId = 1, SkillCode = "csharp", IsMatched = true, Evidence = "C#" });
            await db.SaveChangesAsync();
            return new Seeded(application.ApplicationId, saved.PromptVersionId, evaluation.EvaluationId);
        }

        /// <summary>Chạy NGUYÊN VĂN database/check_a41_khong_tao_dong.sql (chỉ SELECT).</summary>
        public async Task<string> CountsAsync()
        {
            var sql = await File.ReadAllTextAsync(TestSupport.RepoFile("database/check_a41_khong_tao_dong.sql"));
            await using var connection = new SqlConnection(_connection);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return string.Join(", ", Enumerable.Range(0, reader.FieldCount).Select(i => $"{reader.GetName(i)}={reader.GetValue(i)}"));
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (Factory is not null) await Factory.DisposeAsync();
            // Không DROP catalog: giữ lại để kiểm tra, người dùng quyết định dọn dẹp.
        }
    }
}

public sealed class A41SqlFactAttribute : FactAttribute
{
    public const string Setting = "SMARTCV_A41_TEST_SQL_CONNECTION";
    public const string Prefix = "SmartCv_A41_Test_";

    public A41SqlFactAttribute()
    {
        var value = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrWhiteSpace(value))
        {
            Skip = $"NOT RUN: set {Setting} for a new retained synthetic {Prefix} database.";
            return;
        }
        try { Validate(new SqlConnectionStringBuilder(value)); }
        catch (ArgumentException) { Skip = $"NOT RUN: {Setting} must designate {Prefix} without AttachDBFilename."; }
    }

    internal static void Validate(SqlConnectionStringBuilder value)
    {
        if (!value.InitialCatalog.StartsWith(Prefix, StringComparison.Ordinal) || !string.IsNullOrEmpty(value.AttachDBFilename))
            throw new ArgumentException($"Only {Prefix} without attached files is allowed.");
    }
}
