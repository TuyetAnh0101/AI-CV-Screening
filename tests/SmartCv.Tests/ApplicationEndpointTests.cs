using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

/// <summary>A8: POST / GET / DELETE /api/applications qua pipeline HTTP (TestServer, database InMemory có 5 job nền).</summary>
public class ApplicationEndpointTests
{
    private const int InactiveJobId = 99;

    private sealed record Env(WebApplicationFactory<Program> Factory, HttpClient Client) : IDisposable
    {
        public void Dispose() { Client.Dispose(); Factory.Dispose(); }

        public async Task<T> DbAsync<T>(Func<SmartCvDbContext, Task<T>> action)
        {
            using var scope = Factory.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<SmartCvDbContext>());
        }
    }

    private static async Task<Env> CreateAsync()
    {
        var factory = TestSupport.ApiFactory(dbName: Guid.NewGuid().ToString("N"));
        var env = new Env(factory, factory.CreateClient());
        await env.DbAsync(async db =>
        {
            await db.Database.EnsureCreatedAsync();   // nạp 5 job nền (HasData)
            db.Jobs.Add(new Job { JobId = InactiveJobId, Title = "Vị trí đã đóng", IsActive = false, CreatedAt = DateTime.UtcNow });
            return await db.SaveChangesAsync();
        });
        return env;
    }

    private static async Task<Guid> AddCvAsync(Env env, string name, string status = ExtractionStatuses.Completed)
    {
        var id = Guid.NewGuid();
        await env.DbAsync(db =>
        {
            db.Cvs.Add(new Cv
            {
                CvId = id, OriginalFileName = name, StoredPath = $"Uploads/CV/2026/11/{id}.pdf", ContentType = "application/pdf",
                FileSizeBytes = 1, FileHash = id.ToString("N").PadRight(64, '0').ToUpperInvariant(), ExtractionStatus = status,
                UploadedAt = DateTime.UtcNow
            });
            return db.SaveChangesAsync();
        });
        return id;
    }

    private static async Task<(HttpStatusCode Status, CreateApplicationsResponse? Body, ProblemDetails? Problem)> ApplyAsync(
        Env env, int jobId, params Guid[] cvIds)
    {
        using var response = await env.Client.PostAsJsonAsync("/api/applications", new CreateApplicationsRequest { JobId = jobId, CvIds = [.. cvIds] });
        return response.IsSuccessStatusCode
            ? (response.StatusCode, await response.Content.ReadFromJsonAsync<CreateApplicationsResponse>(), null)
            : (response.StatusCode, null, await response.Content.ReadFromJsonAsync<ProblemDetails>());
    }

    [Fact]
    public async Task Creates_applications_in_request_order_with_ids_and_warnings()
    {
        using var env = await CreateAsync();
        var done = await AddCvAsync(env, "a.pdf");
        var failed = await AddCvAsync(env, "b.pdf", ExtractionStatuses.Failed);
        var pending = await AddCvAsync(env, "c.pdf", ExtractionStatuses.Pending);

        var (status, body, _) = await ApplyAsync(env, 1, failed, done, pending);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, body!.JobId);
        Assert.Equal("Backend Developer (.NET)", body.JobTitle);
        Assert.Equal((3, 0, 0), (body.CreatedCount, body.ExistingCount, body.FailedCount));
        Assert.Equal([failed, done, pending], body.Items.Select(i => i.CvId));
        Assert.All(body.Items, i => { Assert.Equal(ApplicationItemStatuses.Created, i.Status); Assert.True(i.ApplicationId > 0); });
        Assert.Equal("b.pdf", body.Items[0].FileName);
        Assert.Contains("không đọc được nội dung", body.Items[0].Message);
        Assert.Null(body.Items[1].Message);
        Assert.Equal(ExtractionStatuses.Pending, body.Items[2].ExtractionStatus);
        Assert.Equal(3, await env.DbAsync(db => db.Applications.CountAsync(a => a.JobId == 1)));
    }

    [Fact]
    public async Task Sending_again_returns_already_exists_with_same_ids_and_no_new_rows()
    {
        using var env = await CreateAsync();
        var cv1 = await AddCvAsync(env, "a.pdf");
        var cv2 = await AddCvAsync(env, "b.pdf");
        var first = (await ApplyAsync(env, 1, cv1, cv2)).Body!;

        var (status, again, _) = await ApplyAsync(env, 1, cv1, cv2);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((0, 2, 0), (again!.CreatedCount, again.ExistingCount, again.FailedCount));
        Assert.All(again.Items, i => Assert.Equal(ApplicationItemStatuses.AlreadyExists, i.Status));
        Assert.Equal(first.Items.Select(i => i.ApplicationId), again.Items.Select(i => i.ApplicationId));
        Assert.Equal("CV đã ứng tuyển vị trí này từ trước.", again.Items[0].Message);
        Assert.Equal(2, await env.DbAsync(db => db.Applications.CountAsync()));
    }

    [Fact]
    public async Task One_cv_can_apply_to_several_jobs()
    {
        using var env = await CreateAsync();
        var cv = await AddCvAsync(env, "a.pdf");

        Assert.Equal(ApplicationItemStatuses.Created, (await ApplyAsync(env, 1, cv)).Body!.Items[0].Status);
        Assert.Equal(ApplicationItemStatuses.Created, (await ApplyAsync(env, 3, cv)).Body!.Items[0].Status);

        var detail = await env.Client.GetFromJsonAsync<CvDetailDto>($"/api/cvs/{cv}");
        Assert.Equal([1, 3], detail!.Applications.Select(a => a.JobId));
    }

    [Fact]
    public async Task Unknown_cv_is_reported_per_item_and_duplicates_or_empty_ids_are_ignored()
    {
        using var env = await CreateAsync();
        var cv = await AddCvAsync(env, "a.pdf");
        var unknown = Guid.NewGuid();

        var (_, body, _) = await ApplyAsync(env, 1, cv, Guid.Empty, unknown, cv);

        Assert.Equal((1, 0, 1), (body!.CreatedCount, body.ExistingCount, body.FailedCount));
        Assert.Equal([cv, unknown], body.Items.Select(i => i.CvId));
        Assert.Equal(ApplicationItemStatuses.CvNotFound, body.Items[1].Status);
        Assert.Null(body.Items[1].ApplicationId);
        Assert.Equal("Không tìm thấy CV.", body.Items[1].Message);
    }

    [Fact]
    public async Task Invalid_requests_return_problem_details()
    {
        using var env = await CreateAsync();
        var cv = await AddCvAsync(env, "a.pdf");

        var notFound = await ApplyAsync(env, 12345, cv);
        Assert.Equal(HttpStatusCode.NotFound, notFound.Status);
        Assert.Equal("Không tìm thấy vị trí", notFound.Problem!.Title);

        var inactive = await ApplyAsync(env, InactiveJobId, cv);
        Assert.Equal(HttpStatusCode.BadRequest, inactive.Status);
        Assert.Equal("Vị trí đã ngừng tuyển", inactive.Problem!.Title);

        var empty = await ApplyAsync(env, 1);
        Assert.Equal(HttpStatusCode.BadRequest, empty.Status);
        Assert.Equal("Chưa chọn CV", empty.Problem!.Title);

        var tooMany = await ApplyAsync(env, 1, [.. Enumerable.Range(0, 101).Select(_ => Guid.NewGuid())]);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.Status);
        Assert.Equal("Quá nhiều CV", tooMany.Problem!.Title);

        using var badJson = await env.Client.PostAsync("/api/applications",
            new StringContent("{\"jobId\":1,\"cvIds\":[\"khong-phai-guid\"]}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);

        Assert.Equal(0, await env.DbAsync(db => db.Applications.CountAsync()));
    }

    [Fact]
    public async Task List_requires_filter_and_filters_by_job_or_cv_with_utc_dates()
    {
        using var env = await CreateAsync();
        var cv1 = await AddCvAsync(env, "Nguyễn Văn An.pdf");
        var cv2 = await AddCvAsync(env, "b.pdf", ExtractionStatuses.Failed);
        await ApplyAsync(env, 1, cv1, cv2);
        await ApplyAsync(env, 2, cv1);

        using var noFilter = await env.Client.GetAsync("/api/applications");
        Assert.Equal(HttpStatusCode.BadRequest, noFilter.StatusCode);

        var byJob = await env.Client.GetFromJsonAsync<List<ApplicationDto>>("/api/applications?jobId=1");
        Assert.Equal(2, byJob!.Count);
        Assert.All(byJob, a => Assert.Equal("Backend Developer (.NET)", a.JobTitle));
        Assert.Contains(byJob, a => a.CvId == cv1 && a.FileName == "Nguyễn Văn An.pdf" && a.CandidateName == null);
        Assert.Contains(byJob, a => a.CvId == cv2 && a.ExtractionStatus == ExtractionStatuses.Failed);

        var byCv = await env.Client.GetFromJsonAsync<List<ApplicationDto>>($"/api/applications?cvId={cv1}");
        Assert.Equal([1, 2], byCv!.Select(a => a.JobId).Order());

        var json = await env.Client.GetStringAsync("/api/applications?jobId=2");
        Assert.Matches("\"createdAt\":\"[0-9T:.-]+Z\"", json);    // giờ UTC có hậu tố Z

        Assert.Empty((await env.Client.GetFromJsonAsync<List<ApplicationDto>>("/api/applications?jobId=4"))!);
    }

    [Fact]
    public async Task Delete_removes_unscored_application_and_returns_404_when_missing()
    {
        using var env = await CreateAsync();
        var cv = await AddCvAsync(env, "a.pdf");
        var id = (await ApplyAsync(env, 2, cv)).Body!.Items[0].ApplicationId!.Value;

        using var deleted = await env.Client.DeleteAsync($"/api/applications/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await env.DbAsync(db => db.Applications.CountAsync()));
        Assert.Equal(1, await env.DbAsync(db => db.Cvs.CountAsync()));     // chỉ bỏ gắn, CV vẫn còn

        using var again = await env.Client.DeleteAsync($"/api/applications/{id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delete_is_blocked_when_application_was_scored_or_labeled(bool scored)
    {
        using var env = await CreateAsync();
        var cv = await AddCvAsync(env, "a.pdf");
        var id = (await ApplyAsync(env, 1, cv)).Body!.Items[0].ApplicationId!.Value;
        await env.DbAsync(db =>
        {
            if (scored) db.Evaluations.Add(new Evaluation { RunId = 1, ApplicationId = id, Status = "Completed", CreatedAt = DateTime.UtcNow });
            else db.HrLabels.Add(new HrLabel { EvaluationSetId = 1, ApplicationId = id, IsPass = true, LabeledBy = "HR", LabeledAt = DateTime.UtcNow });
            return db.SaveChangesAsync();
        });

        using var response = await env.Client.DeleteAsync($"/api/applications/{id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Không thể bỏ gắn", (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        Assert.Equal(1, await env.DbAsync(db => db.Applications.CountAsync()));
    }

    [Fact]
    public async Task Production_does_not_expose_application_endpoints()
    {
        using var factory = TestSupport.ApiFactory("Production");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/applications?jobId=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/applications/1")).StatusCode);
        using var post = await client.PostAsJsonAsync("/api/applications", new CreateApplicationsRequest { JobId = 1, CvIds = [Guid.NewGuid()] });
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.DoesNotContain(factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
            e => e.RoutePattern.RawText?.StartsWith("/api/applications") == true);
    }

    [Fact]
    public async Task Write_endpoints_are_rate_limited()
    {
        using var factory = TestSupport.ApiFactory(dbName: Guid.NewGuid().ToString("N"),
            settings: new() { ["CvUpload:Endpoint:PermitLimit"] = "2" });
        using var client = factory.CreateClient();

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var r = await client.PostAsJsonAsync("/api/applications", new CreateApplicationsRequest { JobId = 1, CvIds = [] });
            codes.Add(r.StatusCode);
        }

        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests], codes);
    }
}
