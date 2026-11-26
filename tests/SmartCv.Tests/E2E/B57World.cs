using System.Net.Http.Headers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartCv.Core.Auth;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;
using SmartCv.Core.Workspaces;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Workspaces;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.E2E;

/// <summary>
/// B57: one isolated API host (real JWT + DB session validation, EF InMemory, temporary storage) with
/// a System Admin, an internal HR member, two Personal workspace Owners and edge-case accounts.
/// Every workspace holds a fully synthetic graph whose text carries a unique marker so any
/// cross-workspace leak is detectable in JSON, XLSX, PDF and CSV output.
/// </summary>
internal sealed class B57World : IDisposable
{
    internal const int Internal = WorkspaceKinds.InternalWorkspaceId;
    internal const int OwnerAWorkspace = 2;
    internal const int OwnerBWorkspace = 3;
    internal const int EmptyWorkspace = 4;
    internal static readonly int[] Workspaces = [Internal, OwnerAWorkspace, OwnerBWorkspace];

    internal static string Marker(int workspace) => workspace switch
    {
        Internal => "B57_INTERNAL_PRIVATE",
        OwnerAWorkspace => "B57_OWNER_A_PRIVATE",
        OwnerBWorkspace => "B57_OWNER_B_PRIVATE",
        _ => "B57_EMPTY_PRIVATE"
    };

    internal static IEnumerable<string> ForeignMarkers(int workspace) => Workspaces.Where(w => w != workspace).Select(Marker);

    internal sealed record Graph(int Job, int[] Candidates, Guid[] Cvs, int[] Applications, int Prompt, int Set, int Run, int[] Evaluations, int Flag);

    internal WebApplicationFactory<Program> Factory { get; }
    internal Dictionary<int, Graph> Data { get; } = [];
    internal string Password { get; } = A28TestSupport.Password();
    internal User Admin { get; private set; } = null!;
    internal User InternalHr { get; private set; } = null!;
    internal User OwnerA { get; private set; } = null!;
    internal User OwnerB { get; private set; } = null!;
    internal User EmptyOwner { get; private set; } = null!;
    internal User Unverified { get; private set; } = null!;
    internal User Locked { get; private set; } = null!;

    internal B57ReportFault? ReportFault { get; }

    internal bool IsSql { get; }

    private B57World(Dictionary<string, string?>? settings, bool faultReports, string? sqlConnection, bool kestrel = false, Action<IServiceCollection>? services = null)
    {
        IsSql = sqlConnection is not null;
        if (faultReports) ReportFault = new B57ReportFault();
        var database = Guid.NewGuid().ToString("N");
        var config = new Dictionary<string, string?>
        {
            ["Scoring:WorkerEnabled"] = "false", ["Retention:AutoAnonymize"] = "false", ["Email:WorkerEnabled"] = "false",
            ["Email:Mode"] = "File", ["Gemini:UseFake"] = "true", ["CvUpload:Endpoint:Enabled"] = "true",
            ["Logging:EventLog:LogLevel:Default"] = "None"
        };
        // Raw-SQL readers (Gemini telemetry) open ConnectionStrings:SmartCvDb themselves: point it at the same disposable catalog.
        if (sqlConnection is not null) config["ConnectionStrings:SmartCvDb"] = sqlConnection;
        if (settings is not null) foreach (var pair in settings) config[pair.Key] = pair.Value;
        Factory = A28TestSupport.Factory(config).WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddDataProtection().UseEphemeralDataProtectionProvider();
            if (ReportFault is not null || sqlConnection is not null)
            {
                s.RemoveAll<DbContextOptions<SmartCvDbContext>>();
                s.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<SmartCvDbContext>>();
                s.AddDbContext<SmartCvDbContext>(o =>
                {
                    // SQL: only the disposable SmartCv_E2E_Test_<guid> catalog created by B57Database (validated there).
                    if (sqlConnection is not null) o.UseSqlServer(sqlConnection); else o.UseInMemoryDatabase(database);
                    if (ReportFault is { } interceptor) o.AddInterceptors(interceptor);
                });
            }
        }));
        if (services is not null) Factory = Factory.WithWebHostBuilder(b => b.ConfigureTestServices(services));
        // Browser tests: a real loopback socket (random port) instead of TestServer.
        if (kestrel) Factory.UseKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0));
    }

    internal static async Task<B57World> CreateAsync(Dictionary<string, string?>? settings = null, bool faultReports = false, string? sqlConnection = null, bool kestrel = false, Action<IServiceCollection>? services = null)
    {
        var world = new B57World(settings, faultReports, sqlConnection, kestrel, services);
        try { await world.SeedAsync(); return world; }
        catch { world.Dispose(); throw; }
    }

    private async Task SeedAsync()
    {
        await A28TestSupport.WithDb(Factory, db => db.EnsureInternalWorkspaceAsync());
        Admin = await A28TestSupport.Seed(Factory, Password, UserRoles.Admin);
        InternalHr = await A28TestSupport.Seed(Factory, Password, UserRoles.Hr);
        // Owners first without membership: SQL Server's UX_Workspaces_PersonalOwner needs a distinct OwnerUserId per Personal workspace.
        OwnerA = await A28TestSupport.Seed(Factory, Password, UserRoles.Hr, memberOfWorkspace: null);
        OwnerB = await A28TestSupport.Seed(Factory, Password, UserRoles.Hr, memberOfWorkspace: null);
        EmptyOwner = await A28TestSupport.Seed(Factory, Password, UserRoles.Hr, memberOfWorkspace: null);
        Unverified = await A28TestSupport.Seed(Factory, Password, UserRoles.Hr, memberOfWorkspace: null);
        Locked = await A28TestSupport.Seed(Factory, Password, UserRoles.Hr, active: false, memberOfWorkspace: null);
        await A28TestSupport.WithDb(Factory, async db =>
        {
            foreach (var (id, name, owner) in new[] { (OwnerAWorkspace, "Không gian B57 A", OwnerA), (OwnerBWorkspace, "Không gian B57 B", OwnerB), (EmptyWorkspace, "Không gian B57 trống", EmptyOwner) })
            {
                // SQL Server: WorkspaceId is an identity column; a fresh migrated catalog only holds workspace 1.
                var workspace = new Workspace { WorkspaceId = IsSql ? 0 : id, Name = name, Kind = WorkspaceKinds.Personal, OwnerUserId = owner.UserId, IsActive = true, CreatedAt = DateTime.UtcNow };
                db.Workspaces.Add(workspace);
                await db.SaveChangesAsync();
                Assert.Equal(id, workspace.WorkspaceId);
                db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.WorkspaceId, UserId = owner.UserId, Role = WorkspaceRoles.Owner, JoinedAt = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
            Assert.Equal(new[] { Internal, OwnerAWorkspace, OwnerBWorkspace, EmptyWorkspace }, await db.Workspaces.OrderBy(w => w.WorkspaceId).Select(w => w.WorkspaceId).ToArrayAsync());
            var unverified = await db.Users.SingleAsync(u => u.UserId == Unverified.UserId);
            unverified.EmailVerifiedAt = null; unverified.SignupSource = SignupSources.Local;
            await db.SaveChangesAsync();
        });
        foreach (var workspace in Workspaces) Data[workspace] = await AddGraphAsync(workspace);
    }

    private async Task<Graph> AddGraphAsync(int w)
    {
        var marker = Marker(w);
        Graph graph = null!;
        await WithDb(w, async db =>
        {
            var created = new DateTime(2026, 11, 10, 3, 0, 0, DateTimeKind.Utc).AddMinutes(w);
            var job = new Job { Title = $"{marker} job", MinYearsExperience = 0, CreatedAt = created };
            var skill = new JobSkill { Job = job, SkillCode = $"b57s{w}", DisplayName = $"{marker} skill", SkillType = SkillTypes.MustHave };
            var prompt = new PromptVersion { VersionName = $"{marker} prompt", SystemPrompt = $"{marker} SYSTEM PROMPT SECRET", IsActive = true, CreatedAt = created };
            var run = new EvaluationRun
            {
                RunType = RunTypes.Screening, PromptVersion = prompt, ModelName = "synthetic-fake", KeywordWeight = .3m, AiWeight = .7m,
                PassThreshold = 70, Status = RunStatuses.Completed, TotalItems = 2, ProcessedItems = 2, CreatedAt = created, CompletedAt = created.AddMinutes(1),
                SystemPromptSnapshot = $"{marker} SNAPSHOT SECRET"
            };
            var apps = new List<Application>(); var evaluations = new List<Evaluation>(); var candidates = new List<Candidate>(); var cvs = new List<Cv>();
            for (var i = 0; i < 2; i++)
            {
                var candidate = new Candidate { FullName = $"{marker} candidate {i}", Email = $"b57-{w}-{i}@example.invalid", PhoneNumber = $"090000{w}{i}00", CreatedAt = created };
                var cv = new Cv
                {
                    CvId = Guid.NewGuid(), Candidate = candidate, OriginalFileName = $"{marker}-cv-{i}.pdf", StoredPath = $"synthetic/b57/{w}/{i}.pdf",
                    ContentType = "application/pdf", FileSizeBytes = 10, FileHash = $"{w}{i}".PadLeft(64, 'b'), UploadedAt = created,
                    ExtractionStatus = "Completed", ExtractedText = $"{marker} CV TEXT SECRET"
                };
                var app = new Application { Cv = cv, Job = job, CreatedAt = created.AddMinutes(i), Stage = i == 0 ? ApplicationStages.Shortlisted : ApplicationStages.Rejected,
                    RejectReason = i == 0 ? null : RejectReasons.MissingSkills };
                var evaluation = new Evaluation
                {
                    Run = run, Application = app, Status = EvaluationStatuses.Completed, CreatedAt = created,
                    KeywordScore = 80 - i * 40 + w, AiScore = 90 - i * 40 + w, FinalScore = 87 - i * 40 + w, ScoreSource = ScoreSources.Combined,
                    FitLabel = i == 0 ? "Good" : "Weak", PredictedPass = i == 0, Reasoning = $"{marker} REASONING SECRET", AiRawResponse = $"{marker} RAW SECRET"
                };
                candidates.Add(candidate); cvs.Add(cv); apps.Add(app); evaluations.Add(evaluation);
            }
            var history = new ApplicationEvent { Application = apps[0], FromStage = ApplicationStages.New, ToStage = ApplicationStages.Shortlisted, Actor = marker, CreatedAt = created.AddHours(1) };
            var flag = new ApplicationFlag { Application = apps[1], Reason = ManualFlagReasons.All[0], CreatedBy = marker, CreatedAt = created, UpdatedAt = created };
            var set = new EvaluationSet { Name = $"{marker} set", Job = job, CreatedAt = created };
            var member = new EvaluationSetMember { EvaluationSet = set, Application = apps[0], AddedBy = marker, AddedAt = created };
            db.AddRange(job, skill, prompt, run, history, flag, set, member);
            db.AddRange(candidates); db.AddRange(cvs); db.AddRange(apps); db.AddRange(evaluations);
            await db.SaveChangesAsync();
            graph = new(job.JobId, [.. candidates.Select(c => c.CandidateId)], [.. cvs.Select(c => c.CvId)], [.. apps.Select(a => a.ApplicationId)],
                prompt.PromptVersionId, set.EvaluationSetId, run.RunId, [.. evaluations.Select(e => e.EvaluationId)], flag.FlagId);
        });
        return graph;
    }

    internal User OwnerOf(int workspace) => workspace switch
    {
        Internal => InternalHr, OwnerAWorkspace => OwnerA, OwnerBWorkspace => OwnerB, EmptyWorkspace => EmptyOwner,
        _ => throw new ArgumentOutOfRangeException(nameof(workspace))
    };

    /// <summary>Business client: token carries the DB-validated workspace of that member.</summary>
    internal HttpClient Business(int workspace) => Client(OwnerOf(workspace), workspace);

    /// <summary>System Admin with the internal membership that seeded Admins have; still no business policy.</summary>
    internal HttpClient AdminClient() => Client(Admin, Internal);

    internal HttpClient Client(User user, int? workspace)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", A28TestSupport.Token(user, workspaceId: workspace));
        return client;
    }

    internal HttpClient Anonymous() => Factory.CreateClient();

    internal async Task WithDb(int workspace, Func<SmartCvDbContext, Task> action)
    {
        using var scope = WorkspaceScope.Begin(workspace);
        await A28TestSupport.WithDb(Factory, action);
    }

    /// <summary>Global (non-workspace) tables: users, memberships, audit.</summary>
    internal Task WithGlobalDb(Func<SmartCvDbContext, Task> action) => A28TestSupport.WithDb(Factory, action);

    public void Dispose() => Factory.Dispose();
}
