extern alias web;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;
using web::SmartCv.Web.Models;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

public class JobAssignmentTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private static ApplicationApiClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new HttpClient(new Handler(send)) { BaseAddress = new Uri("https://api.test/") }, NullLogger<ApplicationApiClient>.Instance);

    private static JobDto ActiveJob => new() { JobId = 1, IsActive = true };
    private static UploadItem Item(Guid? id, UploadItemState state = UploadItemState.Uploaded, bool selected = true) =>
        new() { FileName = "cv.pdf", CvId = id, State = state, Selected = selected };

    [Fact]
    public async Task Sends_only_selected_saved_distinct_cv_ids_and_preserves_partial_results()
    {
        var accepted = Guid.NewGuid();
        var existing = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var client = Client(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/applications", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadFromJsonAsync<CreateApplicationsRequest>(ct);
            Assert.Equal(1, body!.JobId);
            Assert.Equal([accepted, existing, missing], body.CvIds);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new CreateApplicationsResponse
                {
                    JobId = 1, CreatedCount = 1, ExistingCount = 1, FailedCount = 1,
                    Items = [
                        new() { CvId = accepted, ApplicationId = 10, Status = ApplicationItemStatuses.Created },
                        new() { CvId = existing, ApplicationId = 11, Status = ApplicationItemStatuses.AlreadyExists },
                        new() { CvId = missing, Status = ApplicationItemStatuses.CvNotFound, Message = "Không tìm thấy CV." }
                    ]
                })
            };
        });

        var result = await client.AssignSelectedAsync(ActiveJob,
        [
            Item(accepted), Item(accepted), Item(existing, UploadItemState.Existing), Item(missing),
            Item(Guid.NewGuid(), selected: false), Item(null), Item(Guid.Empty),
            Item(Guid.NewGuid(), UploadItemState.Uploading), Item(Guid.NewGuid(), UploadItemState.Rejected),
            Item(Guid.NewGuid(), UploadItemState.Invalid)
        ]);

        Assert.True(result.IsSuccess);
        Assert.Equal((1, 1, 1), (result.Data!.CreatedCount, result.Data.ExistingCount, result.Data.FailedCount));
        Assert.Equal([10, 11, (int?)null], result.Data.Items.Select(i => i.ApplicationId));
        Assert.Equal("Không tìm thấy CV.", result.Data.Items[2].Message);
    }

    [Theory]
    [InlineData(0, true, 1)]
    [InlineData(1, false, 1)]
    [InlineData(1, true, 0)]
    [InlineData(1, true, 101)]
    public async Task Invalid_selection_does_not_send_http(int jobId, bool active, int count)
    {
        var client = Client((_, _) => throw new InvalidOperationException("Không được gửi request"));
        var result = await client.AssignSelectedAsync(new JobDto { JobId = jobId, IsActive = active },
            Enumerable.Range(0, count).Select(_ => Item(Guid.NewGuid())));
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task Missing_job_does_not_send_http()
    {
        var client = Client((_, _) => throw new InvalidOperationException("Không được gửi request"));
        Assert.False((await client.AssignSelectedAsync(null, [Item(Guid.NewGuid())])).IsSuccess);
    }

    [Fact]
    public async Task Http_failure_and_cancellation_follow_shared_client_behavior()
    {
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new { detail = "Công việc đã ngừng tuyển." })
        }));
        var result = await client.AssignSelectedAsync(ActiveJob, [Item(Guid.NewGuid())]);
        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.StatusCode);
        Assert.Equal("Công việc đã ngừng tuyển.", result.ErrorMessage);

        client = Client(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.OK); });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.AssignSelectedAsync(ActiveJob, [Item(Guid.NewGuid())], cts.Token));
    }

    private sealed class BrowserFile(byte[] bytes) : IBrowserFile
    {
        public string Name => "b4-cv.pdf";
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => bytes.Length;
        public string ContentType => "application/pdf";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) => new MemoryStream(bytes);
    }

    [Fact]
    public async Task Real_A6_upload_then_A8_assignment_is_idempotent_in_isolated_database()
    {
        // TestServer + EF InMemory + temporary storage; never connect to the configured SQL database.
        using var api = TestSupport.ApiFactory();
        using (var scope = api.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SmartCvDbContext>().Database.EnsureCreatedAsync();
        using var http = api.CreateClient();
        var jobs = await new JobApiClient(http, NullLogger<JobApiClient>.Instance).GetJobsAsync();
        Assert.True(jobs.IsSuccess);
        var job = jobs.Data!.First(j => j.IsActive);
        var upload = new CvApiClient(http, Options.Create(new CvUploadOptions()), NullLogger<CvApiClient>.Instance);
        var uploaded = await upload.UploadAsync([new BrowserFile(TestSupport.Pdf("B4-isolated"))]);
        Assert.True(uploaded.IsSuccess);
        var cv = Assert.Single(uploaded.Data!.Files);
        Assert.Equal(CvUploadStatuses.Accepted, cv.Status);
        Assert.NotNull(cv.CvId);
        var selected = Item(cv.CvId);
        var unselected = Item(Guid.NewGuid(), selected: false);
        var client = new ApplicationApiClient(http, NullLogger<ApplicationApiClient>.Instance);

        var first = await client.AssignSelectedAsync(job, [selected, unselected]);
        var second = await client.AssignSelectedAsync(job, [selected, unselected]);

        Assert.True(first.IsSuccess, first.ErrorMessage);
        Assert.Equal(1, first.Data!.CreatedCount);
        Assert.True(second.IsSuccess, second.ErrorMessage);
        Assert.Equal((0, 1), (second.Data!.CreatedCount, second.Data.ExistingCount));
        Assert.Equal(first.Data.Items[0].ApplicationId, second.Data.Items[0].ApplicationId);
        using var verify = api.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        Assert.Equal(1, await db.Applications.CountAsync());
        Assert.True(selected.Selected);
        Assert.False(unselected.Selected);
    }
}
