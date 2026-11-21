using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

/// <summary>Chạy trên SQL Server thật, chỉ với database tạm SmartCv_A51_Test_{guid} do chính test tạo và xóa.</summary>
public sealed class A51SqlFactAttribute : FactAttribute
{
    public const string Setting = "SMARTCV_A51_TEST_SQL_CONNECTION";
    public const string Prefix = "SmartCv_A51_Test_";

    public A51SqlFactAttribute()
    {
        var value = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrWhiteSpace(value)) { Skip = $"NOT RUN: set {Setting} (server only; the test creates its own {Prefix}<guid> catalog)."; return; }
        try { Validate(new SqlConnectionStringBuilder(value)); }
        catch (ArgumentException) { Skip = $"NOT RUN: {Setting} must not designate SmartCvDb or attach files."; }
    }

    internal static void Validate(SqlConnectionStringBuilder value)
    {
        if (ApplicationDatabaseGuard.IsApplicationCatalog(value.InitialCatalog) || !string.IsNullOrEmpty(value.AttachDBFilename))
            throw new ArgumentException("Refusing to use the application catalog or an attached file.");
    }
}

internal sealed class A51Database : IAsyncDisposable
{
    internal const string Before = "20261120114152_AddRankingExportLogs";
    internal const string After = "20261121040902_AddWorkspaces";
    private readonly string connection;
    internal string Catalog { get; }

    private A51Database(string connection, string catalog) { this.connection = connection; Catalog = catalog; }

    internal static async Task<A51Database> CreateAsync(string migrateTo)
    {
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A51SqlFactAttribute.Setting));
        A51SqlFactAttribute.Validate(builder);
        var master = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master", Pooling = false, ConnectTimeout = 15 };
        builder.InitialCatalog = A51SqlFactAttribute.Prefix + Guid.NewGuid().ToString("N");
        builder.Pooling = false; builder.ConnectTimeout = 15;
        Assert.Matches("^SmartCv_A51_Test_[0-9a-f]{32}$", builder.InitialCatalog);
        await using (var sql = new SqlConnection(master.ConnectionString))
        {
            await sql.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{builder.InitialCatalog}]; ALTER DATABASE [{builder.InitialCatalog}] SET MULTI_USER;", sql);
            await create.ExecuteNonQueryAsync();
        }
        var db = new A51Database(builder.ConnectionString, builder.InitialCatalog);
        await db.MigrateAsync(migrateTo);
        return db;
    }

    internal DbContextOptions<SmartCvDbContext> Options(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
        new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).AddInterceptors(interceptors).Options;

    internal SmartCvDbContext Context() => new(Options());

    internal async Task MigrateAsync(string target)
    {
        await using var db = Context();
        Assert.Equal(Catalog, db.Database.GetDbConnection().Database);
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var sql = new SqlConnection(connection);
        await sql.OpenAsync();
        await using var check = new SqlCommand("SELECT DB_NAME();", sql);
        Assert.Equal(Catalog, (string?)await check.ExecuteScalarAsync());
        return sql;
    }

    internal async Task ExecAsync(string text)
    {
        await using var sql = await OpenAsync(); await using var command = new SqlCommand(text, sql) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync();
    }

    internal async Task<T> ScalarAsync<T>(string text)
    {
        await using var sql = await OpenAsync(); await using var command = new SqlCommand(text, sql) { CommandTimeout = 60 };
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }

    internal Task<int> IntAsync(string text) => ScalarAsync<int>(text);

    /// <summary>Số lỗi SQL Server (547 FK/CHECK, 2601/2627 trùng, 51031...) hoặc 0 nếu câu lệnh chạy được.</summary>
    internal async Task<int> ErrorNumberAsync(string text)
    {
        try { await ExecAsync(text); return 0; }
        catch (SqlException error) { return error.Number; }
    }

    /// <summary>Chạy script có dòng GO (tách lô như sqlcmd). Trả về số lô đã chạy.</summary>
    internal async Task<int> RunScriptAsync(string script)
    {
        var batches = System.Text.RegularExpressions.Regex.Split(script.TrimStart('﻿'), @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        // Một kết nối cho cả script: BEGIN TRANSACTION / COMMIT của script idempotent nằm ở các lô khác nhau.
        await using var sql = await OpenAsync();
        foreach (var batch in batches)
        {
            await using var command = new SqlCommand(batch, sql) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync();
        }
        return batches.Count;
    }

    /// <summary>Số dòng của các bảng có từ trước A51 (không gồm bảng mới).</summary>
    internal async Task<Dictionary<string, int>> LegacyCountsAsync()
    {
        var counts = new Dictionary<string, int>();
        foreach (var table in new[] { "Users", "Jobs", "JobSkills", "JobSkillAliases", "Candidates", "Cvs", "Applications", "ApplicationEvents", "ApplicationFlags",
                     "ApplicationFlagEvents", "PromptVersions", "EvaluationSets", "EvaluationSetMembers", "HrLabels", "EvaluationRuns", "Evaluations",
                     "EvaluationSkills", "ExportLogs", "AuditEvents", "CandidateDataRequests" })
            counts[table] = await IntAsync($"SELECT COUNT(*) FROM dbo.[{table}]");
        return counts;
    }

    /// <summary>Băm toàn bộ cột định danh/đăng nhập của Users để chứng minh A51 không sửa tài khoản.</summary>
    internal async Task<string> UserCredentialHashAsync()
    {
        await using var sql = await OpenAsync();
        await using var command = new SqlCommand("SELECT UserId, Email, FullName, PasswordHash, Role, CAST(IsActive AS int), CreatedAt, MustChangePassword, SecurityStamp FROM dbo.Users ORDER BY UserId", sql);
        await using var reader = await command.ExecuteReaderAsync();
        var text = new StringBuilder();
        while (await reader.ReadAsync())
            for (var i = 0; i < reader.FieldCount; i++) text.Append(Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).Append('|');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Dữ liệu tổng hợp trước A51: 2 user, job tự tạo + sửa job seed, ứng viên, CV, hồ sơ, prompt, lượt chấm, bộ, nhãn, export, audit.</summary>
    internal async Task SeedLegacyAsync()
    {
        await ExecAsync("""
            INSERT INTO Users (Email, FullName, PasswordHash, Role, IsActive, MustChangePassword, SecurityStamp)
            VALUES (N'admin@a51.test', N'Admin A51', 'AQAAAAEAACcQAAAAEFAKEHASH-ADMIN', N'Admin', 1, 0, 'STAMPADMIN'),
                   (N'hr@a51.test', N'HR A51', 'AQAAAAEAACcQAAAAEFAKEHASH-HR', N'HR', 1, 0, 'STAMPHR'),
                   (N'hr2@a51.test', N'HR Locked A51', 'AQAAAAEAACcQAAAAEFAKEHASH-HR2', N'HR', 0, 1, 'STAMPHR2');
            UPDATE Jobs SET Description = N'Mô tả đã chỉnh sửa bởi HR' WHERE JobId = 1;
            INSERT INTO Jobs (Title, Description, MinYearsExperience, IsActive) VALUES (N'Legacy custom job', N'x', 0, 1);
            INSERT INTO Candidates (FullName, Email) VALUES (N'Ứng viên giả', N'candidate@a51.test');
            INSERT INTO Cvs (CvId, CandidateId, OriginalFileName, StoredPath, ContentType, FileSizeBytes, FileHash, ExtractionStatus)
            VALUES ('11111111-1111-1111-1111-111111111111', 1, N'a.pdf', N'Uploads/CV/a.pdf', N'application/pdf', 10, REPLICATE('a', 64), N'Completed'),
                   ('22222222-2222-2222-2222-222222222222', NULL, N'b.pdf', N'Uploads/CV/b.pdf', N'application/pdf', 10, REPLICATE('b', 64), N'Pending');
            INSERT INTO Applications (CvId, JobId) VALUES ('11111111-1111-1111-1111-111111111111', 1), ('22222222-2222-2222-2222-222222222222', 6);
            INSERT INTO ApplicationEvents (ApplicationId, FromStage, ToStage, Actor) VALUES (1, N'New', N'Reviewing', N'HR A51');
            INSERT INTO PromptVersions (VersionName, SystemPrompt, IsActive) VALUES (N'v1', N'prompt', 1), (N'v2', N'prompt 2', 0);
            INSERT INTO EvaluationSets (Name, JobId) VALUES (N'Bộ giả', 1);
            INSERT INTO HrLabels (EvaluationSetId, ApplicationId, IsPass, LabeledBy) VALUES (1, 1, 1, N'HR A51');
            INSERT INTO EvaluationSetMembers (EvaluationSetId, ApplicationId, AddedBy) VALUES (1, 1, N'HR A51');
            INSERT INTO EvaluationRuns (RunType, PromptVersionId, ModelName, Temperature, KeywordWeight, AiWeight, Status, TotalItems, ProcessedItems)
            VALUES (N'Screening', 1, N'fake-model', 0.2, 0.3, 0.7, N'Completed', 1, 1);
            INSERT INTO EvaluationRuns (RunType, PromptVersionId, EvaluationSetId, ModelName, Temperature, KeywordWeight, AiWeight, Status, TotalItems, ProcessedItems)
            VALUES (N'PromptTest', 1, 1, N'fake-model', 0.2, 0.3, 0.7, N'Completed', 1, 1);
            INSERT INTO Evaluations (RunId, ApplicationId, KeywordScore, AiScore, FinalScore, ScoreSource, FitLabel, PredictedPass, YearsExperience, Status)
            VALUES (1, 1, 70, 80, 77, N'Combined', N'Good', 1, 1, N'Completed');
            INSERT INTO AuditEvents (ActorUserId, ActorNameSnapshot, ActorRoleSnapshot, ActionCode, Category, ResourceType, Outcome, RequestId, SafeSummary)
            VALUES (1, N'Admin A51', N'Admin', 'auth.login', 'auth', 'user', 'Succeeded', 'req-1', N'legacy audit'),
                   (NULL, NULL, NULL, 'system.start', 'system', 'system', 'Succeeded', 'req-2', N'system event');
            INSERT INTO ExportLogs (AuditEventId, RunId, JobId, Format, Stage, Fit, SearchApplied, NeedsReview, ManualFlags, [RowCount], Anonymized)
            VALUES (1, 1, 1, 'xlsx', 'all', 'all', 0, 0, 0, 1, 0);
            """);
    }

    public async ValueTask DisposeAsync()
    {
        var master = new SqlConnectionStringBuilder(connection) { InitialCatalog = "master", Pooling = false };
        if (!Catalog.StartsWith(A51SqlFactAttribute.Prefix, StringComparison.Ordinal)) return;
        await using var sql = new SqlConnection(master.ConnectionString);
        await sql.OpenAsync();
        await using var drop = new SqlCommand($"IF DB_ID(@c) IS NOT NULL BEGIN ALTER DATABASE [{Catalog}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Catalog}]; END", sql);
        drop.Parameters.AddWithValue("@c", Catalog);
        await drop.ExecuteNonQueryAsync();
    }
}

public class A51SqlTests
{
    private static readonly string[] BusinessTables = { "Jobs", "Cvs", "Candidates", "Applications", "PromptVersions", "EvaluationSets", "EvaluationRuns", "ExportLogs" };

    // (1) Database trống: toàn bộ migration từ đầu
    [A51SqlFact]
    public async Task Empty_database_migrates_to_a_valid_workspace_schema_with_seed_jobs_in_workspace_1()
    {
        await using var db = await A51Database.CreateAsync(A51Database.After);

        Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM Workspaces WHERE WorkspaceId = 1 AND Kind = 'Internal' AND IsActive = 1 AND OwnerUserId IS NULL AND Name = N'SCApp nội bộ'"));
        Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM Workspaces"));
        Assert.Equal(0, await db.IntAsync("SELECT COUNT(*) FROM WorkspaceMembers"));
        Assert.Equal(5, await db.IntAsync("SELECT COUNT(*) FROM Jobs WHERE WorkspaceId = 1"));
        Assert.Equal(5, await db.IntAsync("SELECT COUNT(*) FROM Jobs"));
        Assert.True(await db.IntAsync("SELECT COUNT(*) FROM JobSkills") > 0);

        foreach (var table in BusinessTables)
            Assert.Equal(1, await db.IntAsync($"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.{table}') AND name = N'WorkspaceId' AND is_nullable = 0"));
        Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.AuditEvents') AND name = N'WorkspaceId' AND is_nullable = 1"));

        // Chỉ mục, khóa ngoại ghép, ràng buộc cũ đã bỏ.
        Assert.Equal(0, await db.IntAsync("SELECT COUNT(*) FROM sys.indexes WHERE name IN (N'UQ_Jobs_Title', N'UQ_Cvs_FileHash', N'UQ_PromptVersions_VersionName', N'UQ_EvaluationSets_Name', N'UX_PromptVersions_OneActive')"));
        Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM sys.indexes WHERE name = N'UQ_Jobs_Workspace_Title' AND is_unique = 1"));
        Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM sys.indexes WHERE name = N'UX_PromptVersions_Workspace_OneActive' AND is_unique = 1 AND filter_definition LIKE N'%IsActive%'"));
        Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM sys.indexes WHERE name = N'IX_Cvs_Workspace_FileHash'"));
        Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM sys.indexes WHERE name = N'IX_AuditEvents_Workspace_Time'"));
        foreach (var fk in new[] { "FK_Applications_Jobs", "FK_Applications_Cvs", "FK_EvaluationRuns_PromptVersions", "FK_EvaluationRuns_EvaluationSets", "FK_EvaluationSets_Jobs", "FK_Cvs_Candidates" })
            Assert.Equal(2, await db.IntAsync($"SELECT COUNT(*) FROM sys.foreign_key_columns k JOIN sys.foreign_keys f ON f.object_id = k.constraint_object_id WHERE f.name = N'{fk}'"));
        Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM sys.default_constraints WHERE name = N'DF_Users_SignupSource'"));
        Assert.Equal(0, await db.IntAsync("SELECT COUNT(*) FROM sys.default_constraints WHERE name LIKE N'DF_ExportLogs_WorkspaceId%'"));
        foreach (var trigger in new[] { "TR_ApplicationEvents_NoChange", "TR_AuditEvents_NoChange", "TR_ExportLogs_NoChange" })
            Assert.Equal(1, await db.IntAsync($"SELECT COUNT(*) FROM sys.triggers WHERE name = N'{trigger}' AND is_disabled = 0"));

        Assert.False(await HasPendingAsync(db));
        await using var context = db.Context();
        var readiness = await DatabaseSchemaDiagnostics.InspectAsync(context);
        Assert.True(readiness.IsReady, string.Join("; ", readiness.PendingMigrations.Concat(readiness.MissingObjects)));
    }

    private static async Task<bool> HasPendingAsync(A51Database db)
    {
        await using var context = db.Context();
        return (await context.Database.GetPendingMigrationsAsync()).Any();
    }

    // (2) Database có dữ liệu: không mất dòng nào, mọi dòng vào workspace 1, tài khoản không đổi
    [A51SqlFact]
    public async Task Legacy_data_is_preserved_assigned_to_workspace_1_and_accounts_are_untouched()
    {
        await using var db = await A51Database.CreateAsync(A51Database.Before);
        await db.SeedLegacyAsync();
        var before = await db.LegacyCountsAsync();
        var credentials = await db.UserCredentialHashAsync();
        Assert.Equal(6, before["Jobs"]);   // 5 seed + 1 job tự tạo (JobId 6, dùng cho hồ sơ thứ hai)
        Assert.Equal(3, before["Users"]);

        await db.MigrateAsync(A51Database.After);

        Assert.Equal(before, await db.LegacyCountsAsync());
        Assert.Equal(credentials, await db.UserCredentialHashAsync());
        foreach (var table in BusinessTables)
            Assert.Equal(await db.IntAsync($"SELECT COUNT(*) FROM [{table}]"), await db.IntAsync($"SELECT COUNT(*) FROM [{table}] WHERE WorkspaceId = 1"));

        // Job seed người dùng đã sửa vẫn còn đúng nội dung (migration không xóa/chèn lại seed).
        Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM Jobs WHERE JobId = 1 AND Description = N'Mô tả đã chỉnh sửa bởi HR' AND WorkspaceId = 1"));

        // Lịch sử/audit: giữ nguyên, WorkspaceId của audit cũ vẫn NULL, export log cũ thuộc workspace 1.
        Assert.Equal(2, await db.IntAsync("SELECT COUNT(*) FROM AuditEvents WHERE WorkspaceId IS NULL"));
        Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM ExportLogs WHERE WorkspaceId = 1"));
        Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM Evaluations WHERE Status = N'Completed' AND FinalScore = 77"));

        // Membership: mọi user cũ đúng 1 dòng; Admin nhỏ nhất là Owner, người còn lại là Member; Users.Role không đổi.
        Assert.Equal(3, await db.IntAsync("SELECT COUNT(*) FROM WorkspaceMembers WHERE WorkspaceId = 1"));
        Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM WorkspaceMembers m JOIN Users u ON u.UserId = m.UserId WHERE m.Role = 'Owner' AND u.Email = N'admin@a51.test'"));
        Assert.Equal(2, await db.IntAsync("SELECT COUNT(*) FROM WorkspaceMembers WHERE Role = 'Member'"));
        Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM Workspaces w JOIN Users u ON u.UserId = w.OwnerUserId WHERE w.WorkspaceId = 1 AND u.Email = N'admin@a51.test'"));
        Assert.Equal(0, await db.IntAsync("SELECT COUNT(*) FROM Users WHERE Role NOT IN (N'Admin', N'HR')"));
        Assert.Equal(3, await db.IntAsync("SELECT COUNT(*) FROM Users WHERE SignupSource = N'Admin' AND EmailVerifiedAt = CreatedAt AND TermsAcceptedAt IS NULL"));

        // Trigger lịch sử còn hoạt động và vẫn chặn sửa/xóa.
        Assert.Equal(51031, await db.ErrorNumberAsync("UPDATE ExportLogs SET Anonymized = 1"));
        Assert.NotEqual(0, await db.ErrorNumberAsync("UPDATE AuditEvents SET SafeSummary = N'x'"));
        Assert.NotEqual(0, await db.ErrorNumberAsync("DELETE FROM ApplicationEvents"));

        await using (var context = db.Context()) Assert.True((await DatabaseSchemaDiagnostics.InspectAsync(context)).IsReady);
    }

    // (3)(4) Cách ly quan hệ và unique theo workspace
    [A51SqlFact]
    public async Task Database_rejects_cross_workspace_links_and_scopes_uniqueness_per_workspace()
    {
        await using var db = await A51Database.CreateAsync(A51Database.After);
        await db.ExecAsync("""
            INSERT INTO Users (Email, FullName, PasswordHash, Role, IsActive, MustChangePassword, SecurityStamp, SignupSource)
            VALUES (N'p1@a51.test', N'P1', 'h', N'HR', 1, 0, 'S1', 'Local'), (N'p2@a51.test', N'P2', 'h', N'HR', 1, 0, 'S2', 'Local');
            INSERT INTO Workspaces (Name, Kind, OwnerUserId) VALUES (N'WS 2', 'Personal', 1), (N'WS 3', 'Personal', 2);
            """);
        // Workspace 2 = Personal(owner user 1), workspace 3 = Personal(owner user 2)

        // Job cùng tên ở hai workspace: được. Trùng trong cùng workspace: bị từ chối (2601).
        Assert.Equal(0, await db.ErrorNumberAsync("INSERT INTO Jobs (WorkspaceId, Title, MinYearsExperience) VALUES (2, N'Backend Developer (.NET)', 0), (3, N'Backend Developer (.NET)', 0)"));
        Assert.Equal(2601, await db.ErrorNumberAsync("INSERT INTO Jobs (WorkspaceId, Title, MinYearsExperience) VALUES (2, N'Backend Developer (.NET)', 0)"));
        Assert.Equal(2601, await db.ErrorNumberAsync("INSERT INTO Jobs (WorkspaceId, Title, MinYearsExperience) VALUES (1, N'Business Analyst', 0)"));
        var job2 = await db.IntAsync("SELECT JobId FROM Jobs WHERE WorkspaceId = 2");
        var job3 = await db.IntAsync("SELECT JobId FROM Jobs WHERE WorkspaceId = 3");

        // CV: cùng hash khác workspace được, cùng workspace bị từ chối
        const string cvColumns = "(CvId, WorkspaceId, OriginalFileName, StoredPath, ContentType, FileSizeBytes, FileHash, ExtractionStatus)";
        Assert.Equal(0, await db.ErrorNumberAsync($"INSERT INTO Cvs {cvColumns} VALUES ('aaaaaaaa-0000-0000-0000-000000000002', 2, N'a', N'p', N'x', 1, REPLICATE('c', 64), N'Pending'), ('aaaaaaaa-0000-0000-0000-000000000003', 3, N'a', N'p', N'x', 1, REPLICATE('c', 64), N'Pending')"));
        Assert.Equal(2601, await db.ErrorNumberAsync($"INSERT INTO Cvs {cvColumns} VALUES ('aaaaaaaa-0000-0000-0000-000000000004', 2, N'a', N'p', N'x', 1, REPLICATE('c', 64), N'Pending')"));

        // Application: Job và CV cùng workspace -> được; CV khác workspace hoặc Job khác workspace -> FK (547)
        Assert.Equal(0, await db.ErrorNumberAsync($"INSERT INTO Applications (WorkspaceId, CvId, JobId) VALUES (2, 'aaaaaaaa-0000-0000-0000-000000000002', {job2})"));
        Assert.Equal(547, await db.ErrorNumberAsync($"INSERT INTO Applications (WorkspaceId, CvId, JobId) VALUES (2, 'aaaaaaaa-0000-0000-0000-000000000003', {job2})"));   // CV ws3
        Assert.Equal(547, await db.ErrorNumberAsync($"INSERT INTO Applications (WorkspaceId, CvId, JobId) VALUES (3, 'aaaaaaaa-0000-0000-0000-000000000003', {job2})"));   // Job ws2
        Assert.Equal(547, await db.ErrorNumberAsync("INSERT INTO Applications (WorkspaceId, CvId, JobId) VALUES (1, 'aaaaaaaa-0000-0000-0000-000000000003', 1)"));        // CV ws3, ws1 Job
        Assert.Equal(547, await db.ErrorNumberAsync($"INSERT INTO Applications (WorkspaceId, CvId, JobId) VALUES (1, 'aaaaaaaa-0000-0000-0000-000000000002', {job3})"));
        Assert.Equal(547, await db.ErrorNumberAsync("INSERT INTO Applications (WorkspaceId, CvId, JobId) VALUES (99, 'aaaaaaaa-0000-0000-0000-000000000002', 1)"));          // workspace không tồn tại

        // Candidate: CV không được gắn ứng viên workspace khác
        await db.ExecAsync("INSERT INTO Candidates (WorkspaceId, FullName) VALUES (3, N'Ứng viên ws3')");
        var candidate3 = await db.IntAsync("SELECT CandidateId FROM Candidates WHERE WorkspaceId = 3");
        Assert.Equal(547, await db.ErrorNumberAsync($"UPDATE Cvs SET CandidateId = {candidate3} WHERE CvId = 'aaaaaaaa-0000-0000-0000-000000000002'"));
        Assert.Equal(0, await db.ErrorNumberAsync($"UPDATE Cvs SET CandidateId = {candidate3} WHERE CvId = 'aaaaaaaa-0000-0000-0000-000000000003'"));

        // Prompt: mỗi workspace một prompt active; hai prompt active cùng workspace bị từ chối; trùng VersionName chỉ cấm trong workspace
        Assert.Equal(0, await db.ErrorNumberAsync("INSERT INTO PromptVersions (WorkspaceId, VersionName, SystemPrompt, IsActive) VALUES (2, N'v1', N'p', 1), (3, N'v1', N'p', 1)"));
        Assert.Equal(2601, await db.ErrorNumberAsync("INSERT INTO PromptVersions (WorkspaceId, VersionName, SystemPrompt, IsActive) VALUES (2, N'v2', N'p', 1)"));
        Assert.Equal(0, await db.ErrorNumberAsync("INSERT INTO PromptVersions (WorkspaceId, VersionName, SystemPrompt, IsActive) VALUES (2, N'v2', N'p', 0)"));
        Assert.Equal(2627, await db.ErrorNumberAsync("INSERT INTO PromptVersions (WorkspaceId, VersionName, SystemPrompt, IsActive) VALUES (2, N'v2', N'p', 0)"));
        var prompt3 = await db.IntAsync("SELECT PromptVersionId FROM PromptVersions WHERE WorkspaceId = 3");

        // EvaluationRun: PromptVersion / EvaluationSet khác workspace -> 547
        const string runColumns = "(WorkspaceId, RunType, PromptVersionId, EvaluationSetId, ModelName, Temperature, KeywordWeight, AiWeight, Status, TotalItems)";
        Assert.Equal(547, await db.ErrorNumberAsync($"INSERT INTO EvaluationRuns {runColumns} VALUES (2, N'Screening', {prompt3}, NULL, N'm', 0.2, 0.3, 0.7, N'Queued', 0)"));
        Assert.Equal(0, await db.ErrorNumberAsync($"INSERT INTO EvaluationRuns {runColumns} VALUES (3, N'Screening', {prompt3}, NULL, N'm', 0.2, 0.3, 0.7, N'Queued', 0)"));
        Assert.Equal(0, await db.ErrorNumberAsync("INSERT INTO EvaluationSets (WorkspaceId, Name) VALUES (2, N'Bộ A'), (3, N'Bộ A')"));   // trùng tên khác workspace được
        Assert.Equal(2627, await db.ErrorNumberAsync("INSERT INTO EvaluationSets (WorkspaceId, Name) VALUES (2, N'Bộ A')"));
        var set2 = await db.IntAsync("SELECT EvaluationSetId FROM EvaluationSets WHERE WorkspaceId = 2");
        Assert.Equal(547, await db.ErrorNumberAsync($"INSERT INTO EvaluationRuns {runColumns} VALUES (3, N'PromptTest', {prompt3}, {set2}, N'm', 0.2, 0.3, 0.7, N'Queued', 0)"));

        // EvaluationSet -> Job
        Assert.Equal(547, await db.ErrorNumberAsync($"INSERT INTO EvaluationSets (WorkspaceId, Name, JobId) VALUES (3, N'Bộ B', {job2})"));
        Assert.Equal(0, await db.ErrorNumberAsync($"INSERT INTO EvaluationSets (WorkspaceId, Name, JobId) VALUES (3, N'Bộ B', {job3})"));
        Assert.Equal(0, await db.ErrorNumberAsync("INSERT INTO EvaluationSets (WorkspaceId, Name, JobId) VALUES (3, N'Bộ C', NULL)"));   // JobId NULL: không kiểm tra

        // Personal workspace: một Owner user chỉ có một Personal workspace; Internal không bị ràng buộc này
        Assert.Equal(2601, await db.ErrorNumberAsync("INSERT INTO Workspaces (Name, Kind, OwnerUserId) VALUES (N'Thêm', 'Personal', 1)"));
        Assert.Equal(0, await db.ErrorNumberAsync("INSERT INTO Workspaces (Name, Kind, OwnerUserId) VALUES (N'Nội bộ 2', 'Internal', 1)"));
        Assert.NotEqual(0, await db.ErrorNumberAsync("INSERT INTO Workspaces (Name, Kind) VALUES (N'Sai loại', 'Team')"));
        Assert.NotEqual(0, await db.ErrorNumberAsync($"INSERT INTO Workspaces (Name, Kind) VALUES (N'{new string('x', 101)}', 'Personal')"));

        // WorkspaceMembers: khóa chính kép, Role chỉ Owner/Member, tối đa một Owner mỗi workspace
        Assert.Equal(0, await db.ErrorNumberAsync("INSERT INTO WorkspaceMembers (WorkspaceId, UserId, Role) VALUES (2, 1, 'Owner'), (3, 2, 'Owner'), (2, 2, 'Member')"));
        Assert.Equal(2627, await db.ErrorNumberAsync("INSERT INTO WorkspaceMembers (WorkspaceId, UserId, Role) VALUES (2, 2, 'Member')"));
        Assert.Equal(2601, await db.ErrorNumberAsync("INSERT INTO WorkspaceMembers (WorkspaceId, UserId, Role) VALUES (3, 1, 'Owner')"));
        Assert.NotEqual(0, await db.ErrorNumberAsync("INSERT INTO WorkspaceMembers (WorkspaceId, UserId, Role) VALUES (3, 1, 'Admin')"));
        Assert.Equal(547, await db.ErrorNumberAsync("INSERT INTO WorkspaceMembers (WorkspaceId, UserId, Role) VALUES (99, 1, 'Member')"));

        // Users.SignupSource
        Assert.NotEqual(0, await db.ErrorNumberAsync("INSERT INTO Users (Email, FullName, PasswordHash, Role, IsActive, MustChangePassword, SecurityStamp, SignupSource) VALUES (N'bad@a51.test', N'B', 'h', N'HR', 1, 0, 'S', 'facebook')"));
        Assert.NotEqual(0, await db.ErrorNumberAsync("INSERT INTO Users (Email, FullName, PasswordHash, Role, IsActive, MustChangePassword, SecurityStamp) VALUES (N'owner@a51.test', N'B', 'h', N'Owner', 1, 0, 'S')"));

        // UserTokens / UserExternalLogins
        var hash = new string('a', 64);
        Assert.Equal(0, await db.ErrorNumberAsync($"INSERT INTO UserTokens (UserId, Purpose, TokenHash, ExpiresAt) VALUES (1, 'EmailVerification', '{hash}', SYSUTCDATETIME())"));
        Assert.Equal(2601, await db.ErrorNumberAsync($"INSERT INTO UserTokens (UserId, Purpose, TokenHash, ExpiresAt) VALUES (2, 'PasswordReset', '{hash}', SYSUTCDATETIME())"));
        Assert.NotEqual(0, await db.ErrorNumberAsync("INSERT INTO UserTokens (UserId, Purpose, TokenHash, ExpiresAt) VALUES (2, 'PasswordReset', 'short', SYSUTCDATETIME())"));
        Assert.Equal(547, await db.ErrorNumberAsync($"INSERT INTO UserTokens (UserId, Purpose, TokenHash, ExpiresAt) VALUES (999, 'PasswordReset', '{new string('b', 64)}', SYSUTCDATETIME())"));
        Assert.Equal(0, await db.ErrorNumberAsync("INSERT INTO UserExternalLogins (UserId, Provider, ProviderKey) VALUES (1, 'google', 'sub-1')"));
        Assert.Equal(2601, await db.ErrorNumberAsync("INSERT INTO UserExternalLogins (UserId, Provider, ProviderKey) VALUES (2, 'google', 'sub-1')"));    // cùng provider key
        Assert.Equal(2601, await db.ErrorNumberAsync("INSERT INTO UserExternalLogins (UserId, Provider, ProviderKey) VALUES (1, 'google', 'sub-2')"));    // cùng user + provider
        Assert.Equal(0, await db.ErrorNumberAsync("INSERT INTO UserExternalLogins (UserId, Provider, ProviderKey) VALUES (1, 'linkedin', 'sub-1')"));
    }

    // (6) Script idempotent
    [A51SqlFact]
    public async Task Idempotent_script_applies_twice_without_error_and_matches_the_ef_migration_result()
    {
        var script = File.ReadAllText(Path.Combine(A51SchemaTests.FindRepositoryRoot(), "database", "a51_add_workspaces.sql"));
        Assert.StartsWith("﻿", script);   // UTF-8 BOM như script A35
        Assert.Contains("AddWorkspaces", script);
        Assert.DoesNotContain("SmartCvDb", script);

        await using var scripted = await A51Database.CreateAsync(A51Database.Before);
        await scripted.SeedLegacyAsync();
        var before = await scripted.LegacyCountsAsync();
        var credentials = await scripted.UserCredentialHashAsync();

        await scripted.RunScriptAsync(script);
        var afterFirst = await SchemaFingerprintAsync(scripted);
        await scripted.RunScriptAsync(script);     // lần 2: không lỗi, không đổi gì
        Assert.Equal(afterFirst, await SchemaFingerprintAsync(scripted));
        Assert.Equal(before, await scripted.LegacyCountsAsync());
        Assert.Equal(credentials, await scripted.UserCredentialHashAsync());
        Assert.Equal(1, await scripted.IntAsync("SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId = N'" + A51Database.After + "'"));
        Assert.Equal(3, await scripted.IntAsync("SELECT COUNT(*) FROM WorkspaceMembers WHERE WorkspaceId = 1"));
        Assert.Equal(1, await scripted.IntAsync("SELECT COUNT(*) FROM Workspaces"));
        Assert.False(await HasPendingAsync(scripted));

        // Cùng kết quả schema với `dotnet ef database update`
        await using var viaEf = await A51Database.CreateAsync(A51Database.Before);
        await viaEf.SeedLegacyAsync();
        await viaEf.MigrateAsync(A51Database.After);
        Assert.Equal(await SchemaFingerprintAsync(viaEf), afterFirst);
    }

    // Script kiểm tra chỉ đọc
    [A51SqlFact]
    public async Task Check_script_passes_after_migration_fails_before_it_and_never_writes()
    {
        var check = File.ReadAllText(Path.Combine(A51SchemaTests.FindRepositoryRoot(), "database", "check_a51_workspaces.sql"));
        Assert.DoesNotMatch(@"(?im)^\s*(INSERT\s+(INTO\s+)?(?!#A51)|UPDATE\s|DELETE\s|MERGE\s|ALTER\s|DROP\s+(?!TABLE\s+#A51)|CREATE\s+(?!TABLE\s+#A51)|TRUNCATE\s)", System.Text.RegularExpressions.Regex.Replace(check, @"(?s)/\*.*?\*/", ""));

        await using var legacy = await A51Database.CreateAsync(A51Database.Before);
        await legacy.SeedLegacyAsync();
        Assert.Equal(52051, await legacy.ErrorNumberAsync(check));

        await using var migrated = await A51Database.CreateAsync(A51Database.Before);
        await migrated.SeedLegacyAsync();
        await migrated.MigrateAsync(A51Database.After);
        var counts = await migrated.LegacyCountsAsync();
        var credentials = await migrated.UserCredentialHashAsync();
        Assert.Equal(0, await migrated.ErrorNumberAsync(check));
        Assert.Equal(counts, await migrated.LegacyCountsAsync());
        Assert.Equal(credentials, await migrated.UserCredentialHashAsync());

        // Một workspace có 2 prompt active là không thể; nhưng liên kết gián tiếp chéo workspace thì database không chặn -> script phải báo.
        await migrated.ExecAsync("""
            INSERT INTO Workspaces (Name, Kind) VALUES (N'WS lạ', 'Personal');
            INSERT INTO Candidates (WorkspaceId, FullName) VALUES (2, N'x');
            INSERT INTO Cvs (CvId, WorkspaceId, OriginalFileName, StoredPath, ContentType, FileSizeBytes, FileHash, ExtractionStatus)
            VALUES ('bbbbbbbb-0000-0000-0000-000000000002', 2, N'a', N'p', N'x', 1, REPLICATE('d', 64), N'Pending');
            INSERT INTO Jobs (WorkspaceId, Title, MinYearsExperience) VALUES (2, N'Job ws2', 0);
            INSERT INTO Applications (WorkspaceId, CvId, JobId) VALUES (2, 'bbbbbbbb-0000-0000-0000-000000000002', (SELECT JobId FROM Jobs WHERE WorkspaceId = 2));
            INSERT INTO Evaluations (RunId, ApplicationId, Status) VALUES (1, (SELECT MAX(ApplicationId) FROM Applications), N'Pending');
            """);
        Assert.Equal(52051, await migrated.ErrorNumberAsync(check));
    }

    // Down() chạy được trên database thử nghiệm (không dùng trên dữ liệu thật)
    [A51SqlFact]
    public async Task Down_restores_the_previous_schema_on_a_test_database_and_Up_can_be_reapplied()
    {
        await using var db = await A51Database.CreateAsync(A51Database.Before);
        await db.SeedLegacyAsync();
        var before = await db.LegacyCountsAsync();
        await db.MigrateAsync(A51Database.After);
        await db.MigrateAsync(A51Database.Before);

        Assert.Equal(before, await db.LegacyCountsAsync());
        Assert.Equal(0, await db.IntAsync("SELECT COUNT(*) FROM sys.tables WHERE name IN (N'Workspaces', N'WorkspaceMembers', N'UserTokens', N'UserExternalLogins')"));
        Assert.Equal(0, await db.IntAsync("SELECT COUNT(*) FROM sys.columns WHERE name = N'WorkspaceId'"));
        Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM sys.indexes WHERE name = N'UQ_Jobs_Title' AND is_unique = 1"));
        Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM sys.indexes WHERE name = N'UX_PromptVersions_OneActive' AND is_unique = 1"));
        Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'FK_EvaluationSets_Jobs' AND delete_referential_action = 2"));
        Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM sys.triggers WHERE name = N'TR_ExportLogs_NoChange' AND is_disabled = 0"));

        await db.MigrateAsync(A51Database.After);
        Assert.Equal(before, await db.LegacyCountsAsync());
        Assert.Equal(3, await db.IntAsync("SELECT COUNT(*) FROM WorkspaceMembers"));
    }

    private static async Task<string> SchemaFingerprintAsync(A51Database db)
    {
        // Cột, chỉ mục, khóa ngoại, CHECK, trigger: so sánh hai cách áp dụng migration. Bỏ qua tên do EF/SQL tự sinh và bảng lịch sử.
        await using var context = db.Context();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT x FROM (
              SELECT CONCAT('C|', t.name, '.', c.name, '|', ty.name, '|', c.max_length, '|', c.is_nullable) AS x
                FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id WHERE t.name <> '__EFMigrationsHistory'
              UNION ALL SELECT CONCAT('I|', OBJECT_NAME(object_id), '.', name, '|', is_unique, '|', ISNULL(filter_definition, '')) FROM sys.indexes WHERE name IS NOT NULL AND OBJECT_NAME(object_id) <> '__EFMigrationsHistory' AND is_hypothetical = 0
              UNION ALL SELECT CONCAT('F|', OBJECT_NAME(parent_object_id), '.', name, '|', OBJECT_NAME(referenced_object_id), '|', delete_referential_action) FROM sys.foreign_keys
              UNION ALL SELECT CONCAT('K|', OBJECT_NAME(parent_object_id), '.', name, '|', definition) FROM sys.check_constraints
              UNION ALL SELECT CONCAT('D|', OBJECT_NAME(parent_object_id), '.', name) FROM sys.default_constraints WHERE name NOT LIKE 'DF[_][_]%'
              UNION ALL SELECT CONCAT('T|', name, '|', is_disabled) FROM sys.triggers
            ) q ORDER BY x
            """;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows)))) + ":" + rows.Count;
    }
}
