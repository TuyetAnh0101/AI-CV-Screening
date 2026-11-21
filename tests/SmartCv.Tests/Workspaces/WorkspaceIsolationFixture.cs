using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Workspaces;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests;

/// <summary>Real JWT/session validation; independent InMemory database and temporary storage per fixture.</summary>
internal sealed class WorkspaceIsolationFixture : IDisposable
{
    internal WebApplicationFactory<Program> Factory { get; } = A28TestSupport.Factory().WithWebHostBuilder(b =>
        b.ConfigureTestServices(s => s.AddDataProtection().UseEphemeralDataProtectionProvider()));
    internal User HrA { get; private set; } = null!;
    internal User OwnerB { get; private set; } = null!;
    internal User Admin { get; private set; } = null!;
    internal sealed record Graph(int Job, int Candidate, Guid Cv, int Application, int Prompt, int Set, int Run, int Evaluation, int Flag);
    internal Dictionary<int, Graph> Data { get; } = [];

    internal static async Task<WorkspaceIsolationFixture> CreateAsync()
    {
        var fixture = new WorkspaceIsolationFixture();
        try
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<SmartCvDbContext>>();
            await A52Data.SeedAsync(options);
            fixture.HrA = await A28TestSupport.Seed(fixture.Factory, A28TestSupport.Password(), "HR");
            fixture.OwnerB = await A28TestSupport.Seed(fixture.Factory, A28TestSupport.Password(), "HR", memberOfWorkspace: 2, workspaceRole: "Owner");
            fixture.Admin = await A28TestSupport.Seed(fixture.Factory, A28TestSupport.Password());
            foreach (var w in new[] { 1, 2 })
                await fixture.WithDb(w, async db =>
                {
                    var job = await db.Jobs.SingleAsync();
                    var cv = await db.Cvs.SingleAsync();
                    var candidate = await db.Candidates.SingleAsync();
                    candidate.Email = "shared@example.invalid";
                    candidate.PhoneNumber = "0900000000";
                    var second = new Cv { CvId = Guid.NewGuid(), Candidate = candidate, OriginalFileName = $"a53-{w}.pdf", StoredPath = $"synthetic/{w}/second.pdf", ContentType = "application/pdf", FileSizeBytes = 1, FileHash = new string('f', 64), UploadedAt = DateTime.UtcNow };
                    db.Add(new Application { Cv = second, Job = job, CreatedAt = DateTime.UtcNow });
                    var run = await db.EvaluationRuns.SingleAsync();
                    db.Add(new ExportLog { RunId = run.RunId, JobId = job.JobId, Stage = "all", Fit = "all", RowCount = 1,
                        AuditEvent = new AuditEvent { WorkspaceId = w, ActionCode = "export.ranking", Category = "Export", ResourceType = "run", Outcome = "Succeeded", RequestId = $"a53-{w}", SafeSummary = $"Synthetic workspace {w}", OccurredAtUtc = DateTime.UtcNow } });
                    var workspace = await db.Workspaces.SingleAsync(x => x.WorkspaceId == w);
                    workspace.OwnerUserId = w == 1 ? fixture.Admin.UserId : fixture.OwnerB.UserId;
                    await db.SaveChangesAsync();
                    fixture.Data[w] = new(job.JobId, candidate.CandidateId, cv.CvId,
                        (await db.Applications.SingleAsync(a => a.CvId == cv.CvId)).ApplicationId,
                        (await db.PromptVersions.SingleAsync()).PromptVersionId,
                        (await db.EvaluationSets.SingleAsync()).EvaluationSetId, run.RunId,
                        (await db.Evaluations.SingleAsync()).EvaluationId, (await db.ApplicationFlags.SingleAsync()).FlagId);
                });
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    internal HttpClient Client(int workspace, bool admin = false)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", A28TestSupport.Token(admin ? Admin : workspace == 1 ? HrA : OwnerB, workspaceId: workspace));
        return client;
    }

    internal async Task WithDb(int workspace, Func<SmartCvDbContext, Task> action)
    {
        using var ambient = WorkspaceScope.Begin(workspace);
        await A28TestSupport.WithDb(Factory, action);
    }
    public void Dispose() => Factory.Dispose();
}
