using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Contracts;
using SmartCv.Core.Workspaces;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Email;

namespace SmartCv.Tests.E2E;

/// <summary>
/// Opt-in: SMARTCV_B57_TEST_SQL_CONNECTION names a SQL Server (its Initial Catalog is ignored). Each test creates,
/// migrates and finally drops ONLY its own SmartCv_E2E_Test_&lt;guid&gt; catalog. SmartCvDb is refused here and,
/// independently, by ApplicationDatabaseGuard for every connection/command of the test process.
/// </summary>
public sealed class B57SqlFactAttribute : FactAttribute
{
    public const string Setting = "SMARTCV_B57_TEST_SQL_CONNECTION";
    public B57SqlFactAttribute()
    {
        var value = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrWhiteSpace(value)) { Skip = $"NOT RUN: set {Setting} (server only; the test creates its own {B57Database.Prefix}<guid> catalog)."; return; }
        try { B57Database.Validate(new SqlConnectionStringBuilder(value)); }
        catch (ArgumentException) { Skip = $"NOT RUN: {Setting} must not designate SmartCvDb or attach files."; }
    }
}

internal sealed class B57Database : IAsyncDisposable
{
    internal const string Prefix = "SmartCv_E2E_Test_";
    private readonly string master;
    internal string Connection { get; }
    internal string Catalog { get; }

    private B57Database(string master, string connection, string catalog) { this.master = master; Connection = connection; Catalog = catalog; }

    internal static void Validate(SqlConnectionStringBuilder value)
    {
        if (ApplicationDatabaseGuard.IsApplicationCatalog(value.InitialCatalog) || !string.IsNullOrEmpty(value.AttachDBFilename) || string.IsNullOrWhiteSpace(value.DataSource))
            throw new ArgumentException("Refusing the application catalog, an attached file or an empty server.");
    }

    internal static async Task<B57Database> CreateAsync()
    {
        var server = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(B57SqlFactAttribute.Setting));
        Validate(server);
        var catalog = Prefix + Guid.NewGuid().ToString("N");
        Assert.Matches("^SmartCv_E2E_Test_[0-9a-f]{32}$", catalog);
        var master = new SqlConnectionStringBuilder(server.ConnectionString) { InitialCatalog = "master", Pooling = false, ConnectTimeout = 15 };
        var target = new SqlConnectionStringBuilder(server.ConnectionString) { InitialCatalog = catalog, ConnectTimeout = 15, MultipleActiveResultSets = false };
        await using (var sql = new SqlConnection(master.ConnectionString))
        {
            await sql.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{catalog}]; ALTER DATABASE [{catalog}] SET MULTI_USER;", sql);
            await create.ExecuteNonQueryAsync();
        }
        var db = new B57Database(master.ConnectionString, target.ConnectionString, catalog);
        try
        {
            await using var context = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(db.Connection).Options);
            Assert.Equal(catalog, context.Database.GetDbConnection().Database);
            await context.Database.MigrateAsync();
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        // Drop exactly the catalog this instance created; never a pattern, never another database.
        Assert.Matches("^SmartCv_E2E_Test_[0-9a-f]{32}$", Catalog);
        SqlConnection.ClearAllPools();
        await using var sql = new SqlConnection(master);
        await sql.OpenAsync();
        await using var drop = new SqlCommand($"IF DB_ID(N'{Catalog}') IS NOT NULL BEGIN ALTER DATABASE [{Catalog}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Catalog}]; END", sql);
        await drop.ExecuteNonQueryAsync();
    }
}

public sealed class B57SqlServerTests
{
    /// <summary>Admin Ops endpoints implemented with raw SQL readers (refuse EF InMemory explicitly).</summary>
    internal static readonly string[] SqlOnlyAdminRoutes =
    [
        "/api/admin/ops/overview", "/api/admin/ops/ai-usage", "/api/admin/ops/ai-history", "/api/admin/ops/gemini-telemetry",
        "/api/admin/ops/workspaces", "/api/admin/ops/integrations", "/api/admin/ops/integrations/insights", "/api/health/database"
    ];

    private static Task<B57World> World(B57Database database, Dictionary<string, string?>? settings = null) =>
        B57World.CreateAsync(settings, sqlConnection: database.Connection);

    [B57SqlFact]
    public async Task Sql_E4_R3_R6_dashboard_lists_and_report_files_are_scoped_per_workspace_on_SQL_Server()
    {
        await using var database = await B57Database.CreateAsync();
        using var world = await World(database);
        foreach (var w in B57World.Workspaces)
        {
            using var http = world.Business(w);
            var dashboard = await http.GetStringAsync("/api/dashboard/summary");
            using (var json = JsonDocument.Parse(dashboard))
            {
                Assert.Equal(2, json.RootElement.GetProperty("totalApplications").GetInt32());
                Assert.Equal(2, json.RootElement.GetProperty("totalCvs").GetInt32());
            }
            foreach (var path in new[] { "/api/jobs", "/api/cvs", "/api/candidates", "/api/runs", "/api/prompts", "/api/evaluation-sets", "/api/dashboard/trend", "/api/hr/reports/rows?anonymous=false" })
            {
                var body = await http.GetStringAsync(path);
                if (path != "/api/dashboard/trend") Assert.Contains(B57World.Marker(w), body);
                foreach (var foreign in B57World.ForeignMarkers(w)) Assert.DoesNotContain(foreign, body);
            }
            Assert.DoesNotContain(B57World.ForeignMarkers(w), foreign => dashboard.Contains(foreign));
            var summary = (await http.GetFromJsonAsync<HrReportSummaryDto>("/api/hr/reports/summary"))!;
            Assert.Equal(2, summary.Applications); Assert.Equal(1, summary.PassedScore);
            using var excel = await http.GetAsync("/api/hr/reports/export?anonymous=false");
            Assert.Equal(HttpStatusCode.OK, excel.StatusCode);
            var cells = B57ReportScopeTests.Cells(await excel.Content.ReadAsByteArrayAsync(), out var book);
            using (book) Assert.Equal(3, book.Worksheet("Ho so").LastRowUsed()!.RowNumber());
            Assert.Contains(cells, c => c.Contains(B57World.Marker(w)));
            foreach (var foreign in B57World.ForeignMarkers(w)) Assert.DoesNotContain(cells, c => c.Contains(foreign));
            using var ranking = await http.GetAsync($"/api/runs/{world.Data[w].Run}/ranking/export?stage=all&anonymous=false");
            Assert.Equal(HttpStatusCode.OK, ranking.StatusCode);
            var rankingCells = B57ReportScopeTests.Cells(await ranking.Content.ReadAsByteArrayAsync(), out var rankingBook);
            using (rankingBook) foreach (var foreign in B57World.ForeignMarkers(w)) Assert.DoesNotContain(rankingCells, c => c.Contains(foreign));
            // Foreign ids on SQL are indistinguishable from missing ones.
            var other = world.Data[w == B57World.OwnerAWorkspace ? B57World.OwnerBWorkspace : B57World.OwnerAWorkspace];
            foreach (var path in new[] { $"/api/runs/{other.Run}/ranking/export", $"/api/hr/reports/export?jobId={other.Job}", $"/api/cvs/{other.Cvs[0]}" })
                Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(path)).StatusCode);
        }
    }

    [B57SqlFact]
    public async Task Sql_R2_R7_admin_ops_routes_are_aggregate_only_for_Admin_and_forbidden_for_HR()
    {
        await using var database = await B57Database.CreateAsync();
        using var world = await World(database);
        using var admin = world.AdminClient();
        using var hr = world.Business(B57World.Internal);
        using var owner = world.Business(B57World.OwnerAWorkspace);
        foreach (var path in SqlOnlyAdminRoutes)
        {
            using var response = await admin.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {body[..Math.Min(400, body.Length)]}");
            foreach (var w in B57World.Workspaces)
                foreach (var secret in new[] { " CV TEXT SECRET", " candidate ", " SYSTEM PROMPT SECRET", " SNAPSHOT SECRET", " REASONING SECRET", " RAW SECRET" })
                    Assert.DoesNotContain(B57World.Marker(w) + secret, body);
            Assert.DoesNotContain("AQAAAA", body);
            Assert.Equal(HttpStatusCode.Forbidden, (await hr.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(path)).StatusCode);
        }
    }

    [B57SqlFact]
    public async Task Sql_E10_concurrent_runs_never_exceed_the_daily_quota_of_3()
    {
        await using var database = await B57Database.CreateAsync();
        using var world = await World(database);
        await world.WithGlobalDb(async db =>
        {
            (await db.Workspaces.SingleAsync(w => w.WorkspaceId == B57World.OwnerAWorkspace)).DailyAiEvaluationLimit = 3;
            await db.SaveChangesAsync();
        });
        var app = world.Data[B57World.OwnerAWorkspace].Applications[0];
        var clients = Enumerable.Range(0, 6).Select(_ => world.Business(B57World.OwnerAWorkspace)).ToList();
        try
        {
            var responses = await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync("/api/runs", new { applicationIds = new[] { app } })));
            var codes = responses.Select(r => (int)r.StatusCode).Order().ToArray();
            Assert.Equal(new[] { 201, 201, 201, 429, 429, 429 }, codes);
            foreach (var r in responses) r.Dispose();
        }
        finally { foreach (var c in clients) c.Dispose(); }
        await world.WithDb(B57World.OwnerAWorkspace, async db =>
        {
            Assert.Equal(4, await db.EvaluationRuns.CountAsync());
            Assert.Equal(3, await db.AiUsageEntries.SumAsync(e => e.Units - e.UnitsReleased));
        });
    }

    [B57SqlFact]
    public async Task Sql_E2_full_CV_flow_with_prompt_activation_on_SQL_Server()
    {
        await using var database = await B57Database.CreateAsync();
        using var world = await World(database);
        await B57CvFlowTests.RunFlowAsync(world, activatePromptViaApi: true);
    }

    private static async Task<string> NextEmailTokenAsync(B57World world, string pickup, string expectedPath)
    {
        using (var scope = world.Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<EmailDispatchProcessor>().RunOnceAsync();
        var file = Directory.EnumerateFiles(pickup, "scapp-email-*.eml").OrderByDescending(f => f, StringComparer.Ordinal).First();
        var message = MimeKit.MimeMessage.Load(file);
        var text = message.TextBody ?? message.HtmlBody ?? "";
        var match = Regex.Match(text, @"https://localhost:7001/" + expectedPath + @"\?token=([A-Za-z0-9_-]{43})");
        Assert.True(match.Success, "No " + expectedPath + " link in the synthetic e-mail.");
        return match.Groups[1].Value;
    }

    [B57SqlFact]
    public async Task Sql_E1_E6_E7_register_verify_once_login_personal_workspace_reset_password_with_File_email()
    {
        await using var database = await B57Database.CreateAsync();
        var pickup = TestSupport.TempRoot();
        try
        {
            using var world = await World(database, new()
            {
                ["Registration:Enabled"] = "true", ["Registration:EmailPasswordEnabled"] = "true", ["App:WebBaseUrl"] = "https://localhost:7001",
                ["Email:Mode"] = "File", ["Email:PickupDirectory"] = pickup, ["Email:WorkerEnabled"] = "false"
            });
            using var http = world.Anonymous();
            var email = "b57-new-" + Guid.NewGuid().ToString("N")[..12] + "@example.invalid";
            const string password = "B57Synthetic9Password";
            using (var register = await http.PostAsJsonAsync("/api/auth/register", new RegisterRequest { FullName = "Người dùng B57", Email = email, Password = password, AcceptTerms = true }))
                Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
            var firstToken = await NextEmailTokenAsync(world, pickup, "verify-email");

            // E7: not verified yet → explicit 403 code, no token issued.
            using (var early = await http.PostAsJsonAsync("/api/auth/login", new { email, password }))
            {
                Assert.Equal(HttpStatusCode.Forbidden, early.StatusCode);
                Assert.Contains("email_not_verified", await early.Content.ReadAsStringAsync());
            }
            using (var resend = await http.PostAsJsonAsync("/api/auth/resend-verification", new { email }))
                Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);
            var secondToken = await NextEmailTokenAsync(world, pickup, "verify-email");
            Assert.NotEqual(firstToken, secondToken);
            foreach (var bad in new[] { "", "x", new string('A', 43) })
                Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest { Token = bad })).StatusCode);
            // Expiry: age the newest verification token in the disposable catalog only.
            await world.WithGlobalDb(async db =>
            {
                var hash = SmartCv.Infrastructure.Auth.UserTokenService.HashToken(secondToken);
                var row = await db.UserTokens.SingleAsync(t => t.TokenHash == hash);
                row.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
                await db.SaveChangesAsync();
            });
            using (var expired = await http.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest { Token = secondToken }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
                Assert.Contains("token_expired", await expired.Content.ReadAsStringAsync());
            }
            using (var resend = await http.PostAsJsonAsync("/api/auth/resend-verification", new { email }))
                Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);
            var thirdToken = await NextEmailTokenAsync(world, pickup, "verify-email");
            using var verified = await http.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest { Token = thirdToken });
            Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
            var session = (await verified.Content.ReadFromJsonAsync<LoginResponse>())!;
            Assert.Equal(WorkspaceKinds.Personal, session.User.Workspace!.Kind);
            Assert.Equal(WorkspaceRoles.Owner, session.User.Workspace.Role);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest { Token = thirdToken })).StatusCode); // single use

            using var owner = world.Anonymous();
            owner.DefaultRequestHeaders.Authorization = new("Bearer", session.Token);
            var workspace = await owner.GetStringAsync("/api/workspace");
            Assert.Contains("Personal", workspace);
            var summary = (await owner.GetFromJsonAsync<HrReportSummaryDto>("/api/hr/reports/summary"))!;
            Assert.Equal(0, summary.Applications); // brand-new workspace sees nobody else's data
            var jobs = await owner.GetStringAsync("/api/jobs");
            Assert.DoesNotContain("B57_", jobs);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/users")).StatusCode);

            // E6: reset password → old JWT revoked, token single-use, new password works.
            using (var forgot = await http.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest { Email = email }))
                Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
            var reset = await NextEmailTokenAsync(world, pickup, "reset-password");
            const string newPassword = "B57Synthetic9Changed";
            using (var done = await http.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest { Token = reset, NewPassword = newPassword }))
                Assert.True(done.IsSuccessStatusCode, ((int)done.StatusCode) + " " + await done.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/jobs")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest { Token = reset, NewPassword = "B57Synthetic9Again" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/auth/login", new { email, password })).StatusCode);
            using var relogin = await http.PostAsJsonAsync("/api/auth/login", new { email, password = newPassword });
            Assert.Equal(HttpStatusCode.OK, relogin.StatusCode);
        }
        finally { Directory.Delete(pickup, recursive: true); }
    }
}
