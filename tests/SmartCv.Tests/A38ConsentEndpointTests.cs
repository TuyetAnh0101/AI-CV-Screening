extern alias web;

using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Core.Storage;
using SmartCv.Core.Uploads;
using SmartCv.Infrastructure.Data;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;
public class A38ConsentEndpointTests
{
    private static WebApplicationFactory<Program> Factory() => TestSupport.ApiFactory(settings: new() { ["Scoring:WorkerEnabled"] = "false" });
    private static MultipartFormDataContent Form(int files, string? channel, string? consent)
    {
        var content = new MultipartFormDataContent();
        for (var i = 0; i < files; i++)
            content.Add(new ByteArrayContent(TestSupport.Pdf("A38-" + i)), "files", $"synthetic-{i}.pdf");
        if (channel is not null)
            content.Add(new StringContent(channel), "sourceChannel");
        if (consent is not null)
            content.Add(new StringContent(consent), "consentConfirmed");
        return content;
    }

    private sealed class SpyUpload : ICvUploadService
    {
        public int Calls;
        public Task<CvUploadResponse> UploadAsync(IReadOnlyList<CvUploadFile> files, CvUploadSource source, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("Invalid request must never reach upload service");
        }
    }

    [Theory]
    [InlineData(1, null, null)]
    [InlineData(2, null, null)]
    [InlineData(1, "Email", null)]
    [InlineData(2, "Email", "false")]
    [InlineData(1, null, "true")]
    [InlineData(2, "", "true")]
    [InlineData(2, "Facebook", "true")]
    [InlineData(1, "email", "true")]
    [InlineData(2, "Email", "yes")]
    public async Task Invalid_source_or_consent_rejects_entire_request_before_service_or_storage(int files, string? channel, string? consent)
    {
        var spy = new SpyUpload();
        using var f = Factory().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ICvUploadService>();
            s.AddSingleton<ICvUploadService>(spy);
        }));
        using var http = f.CreateClient();
        using var content = Form(files, channel, consent);
        using var response = await http.PostAsync("/api/cvs/upload", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, spy.Calls);
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        await db.Database.EnsureCreatedAsync();
        Assert.Empty(await db.Cvs.ToListAsync());
    }

    [Theory]
    [InlineData("Email")]
    [InlineData("Website")]
    [InlineData("JobBoard")]
    [InlineData("Referral")]
    [InlineData("Other")]
    public async Task Valid_upload_reads_back_canonical_source_and_UTC_consent_and_duplicate_never_rewrites_metadata(string channel)
    {
        using var f = Factory();
        using var http = f.CreateClient();
        var before = DateTime.UtcNow;
        using var content = Form(2, channel, "true");
        using var response = await http.PostAsync("/api/cvs/upload", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CvUploadResponse>())!;
        Assert.Equal(2, body.SuccessCount);
        foreach (var file in body.Files)
        {
            var detail = (await http.GetFromJsonAsync<CvDetailDto>($"/api/cvs/{file.CvId}"))!;
            Assert.Equal(channel, detail.SourceChannel);
            Assert.NotEmpty(detail.SourceChannelName);
            Assert.True(detail.FileAvailable);
            Assert.InRange(detail.ConsentConfirmedAt!.Value, before, DateTime.UtcNow);
            Assert.Equal(DateTimeKind.Utc, detail.ConsentConfirmedAt.Value.Kind);
            Assert.Null(detail.RetainUntil);
            Assert.Null(detail.AnonymizedAt);
            Assert.False(detail.IsAnonymized);
        }

        using var duplicate = Form(2, "Other", "true");
        using var repeated = await http.PostAsync("/api/cvs/upload", duplicate);
        var rejected = (await repeated.Content.ReadFromJsonAsync<CvUploadResponse>())!;
        Assert.Equal(0, rejected.SuccessCount);
        Assert.All(rejected.Files, x => Assert.Equal("Duplicate", x.ErrorCode));
        var again = (await http.GetFromJsonAsync<CvDetailDto>($"/api/cvs/{body.Files[0].CvId}"))!;
        Assert.Equal(channel, again.SourceChannel);
        using var scope = f.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<SmartCvDbContext>().Cvs.CountAsync());
    }

    [Fact]
    public async Task Legacy_null_metadata_is_explicit_and_anonymized_flag_only_reflects_stored_timestamp()
    {
        using var f = Factory();
        using var http = f.CreateClient();
        Guid id;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            await db.Database.EnsureCreatedAsync();
            var cv = LegacyCv();
            db.Add(cv);
            await db.SaveChangesAsync();
            id = cv.CvId;
        }

        var detail = (await http.GetFromJsonAsync<CvDetailDto>($"/api/cvs/{id}"))!;
        Assert.Null(detail.SourceChannel);
        Assert.Null(detail.ConsentConfirmedAt);
        Assert.Null(detail.RetainUntil);
        Assert.Null(detail.AnonymizedAt);
        Assert.Equal("Chưa ghi (CV tải trước A38)", detail.SourceChannelName);
        Assert.False(detail.IsAnonymized);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            (await db.Cvs.SingleAsync()).AnonymizedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        Assert.True((await http.GetFromJsonAsync<CvDetailDto>($"/api/cvs/{id}"))!.IsAnonymized);
    }

    internal static Cv LegacyCv() => new()
    {
        CvId = Guid.NewGuid(),
        OriginalFileName = "synthetic-legacy.pdf",
        StoredPath = "test-only",
        ContentType = "application/pdf",
        FileSizeBytes = 1,
        FileHash = Guid.NewGuid().ToString("N").PadRight(64, '0'),
        ExtractionStatus = "Completed"
    };
    [Fact]
    public async Task Single_and_bulk_retention_preserve_later_deadline_noops_and_reopening_without_extra_events()
    {
        using var f = Factory();
        using var http = f.CreateClient();
        List<int> ids;
        Guid cvId;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            await db.Database.EnsureCreatedAsync();
            var cv = LegacyCv();
            var apps = new[]
            {
                new Application
                {
                    Cv = cv,
                    JobId = 1
                },
                new Application
                {
                    Cv = cv,
                    JobId = 2
                }
            };
            db.AddRange(apps);
            await db.SaveChangesAsync();
            ids = apps.Select(a => a.ApplicationId).ToList();
            cvId = cv.CvId;
        }

        var now = DateTime.UtcNow;
        using var first = await http.PatchAsJsonAsync($"/api/applications/{ids[0]}/stage", new ChangeStageRequest { ToStage = "Rejected", RejectReason = "MissingSkills", Actor = "HR" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var saved = (await http.GetFromJsonAsync<CvDetailDto>($"/api/cvs/{cvId}"))!.RetainUntil!.Value;
        Assert.InRange(saved, now.AddMonths(6), DateTime.UtcNow.AddMonths(6));
        using var noop = await http.PatchAsJsonAsync($"/api/applications/{ids[0]}/stage", new ChangeStageRequest { ToStage = "Rejected", RejectReason = "MissingSkills", Actor = "HR" });
        Assert.Equal("Unchanged", (await noop.Content.ReadFromJsonAsync<StageChangeResultDto>())!.Outcome);
        Assert.Equal(saved, (await http.GetFromJsonAsync<CvDetailDto>($"/api/cvs/{cvId}"))!.RetainUntil);
        var later = DateTime.UtcNow.AddYears(2);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            (await db.Cvs.SingleAsync()).RetainUntil = later;
            await db.SaveChangesAsync();
        }

        using var bulk = await http.PostAsJsonAsync("/api/applications/stage", new BulkChangeStageRequest { ApplicationIds = ids, ToStage = "OnHold", Actor = "HR" });
        Assert.Equal(2, (await bulk.Content.ReadFromJsonAsync<BulkStageChangeResponse>())!.Changed);
        using var reopen = await http.PatchAsJsonAsync($"/api/applications/{ids[0]}/stage", new ChangeStageRequest { ToStage = "Interview", Actor = "HR" });
        Assert.Equal(HttpStatusCode.OK, reopen.StatusCode);
        Assert.Equal(later, (await http.GetFromJsonAsync<CvDetailDto>($"/api/cvs/{cvId}"))!.RetainUntil);
        using var check = f.Services.CreateScope();
        Assert.Equal(4, await check.ServiceProvider.GetRequiredService<SmartCvDbContext>().ApplicationEvents.CountAsync());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("121")]
    public void Invalid_retention_configuration_stops_startup(string months)
    {
        using var f = TestSupport.ApiFactory(settings: new() { ["Retention:Months"] = months, ["Scoring:WorkerEnabled"] = "false" });
        Assert.Throws<OptionsValidationException>(() => f.CreateClient());
    }

    private sealed class BrowserFile : IBrowserFile
    {
        private readonly byte[] bytes = TestSupport.Pdf("current-Web-without-B38");
        public string Name => "legacy-client.pdf";
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => bytes.Length;
        public string ContentType => "application/pdf";

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) => new MemoryStream(bytes);
    }

    [Fact]
    public async Task Current_Web_upload_without_B38_gets_400_and_saves_nothing_no_consent_is_fabricated()
    {
        using var f = Factory();
        using var http = f.CreateClient();
        var client = new CvApiClient(http, Options.Create(new CvUploadOptions()), NullLogger<CvApiClient>.Instance);
        var result = await client.UploadAsync([new BrowserFile()]);
        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.StatusCode);
        Assert.Contains("consentConfirmed", result.ErrorMessage);
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        await db.Database.EnsureCreatedAsync();
        Assert.Empty(await db.Cvs.ToListAsync());
    }

    [A38DemoFact]
    public async Task Both_demo_CV_images_upload_with_consent_and_can_be_read_back_without_running_OCR_or_AI()
    {
        using var zip = ZipFile.OpenRead(Environment.GetEnvironmentVariable(A38DemoFactAttribute.Setting)!);
        using var f = Factory();
        using var http = f.CreateClient();
        using var form = new MultipartFormDataContent();
        foreach (var name in new[]
        {
            "cv-anh-ro.png",
            "cv-anh-mo.png"
        }

        )
        {
            var entry = Assert.Single(zip.Entries, e => e.Name == name);
            using var input = entry.Open();
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer);
            var file = new ByteArrayContent(buffer.ToArray());
            file.Headers.ContentType = new("image/png");
            form.Add(file, "files", name);
        }

        form.Add(new StringContent("Referral"), "sourceChannel");
        form.Add(new StringContent("true"), "consentConfirmed");
        using var response = await http.PostAsync("/api/cvs/upload", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CvUploadResponse>())!;
        Assert.Equal(2, body.SuccessCount);
        foreach (var uploaded in body.Files)
        {
            var detail = (await http.GetFromJsonAsync<CvDetailDto>($"/api/cvs/{uploaded.CvId}"))!;
            Assert.Equal("Referral", detail.SourceChannel);
            Assert.NotNull(detail.ConsentConfirmedAt);
            Assert.True(detail.FileAvailable);
            using var original = await http.GetAsync($"/api/cvs/{uploaded.CvId}/file");
            Assert.Equal(HttpStatusCode.OK, original.StatusCode);
            Assert.Equal("image/png", original.Content.Headers.ContentType!.MediaType);
        }
    }
}

public sealed class A38DemoFactAttribute : FactAttribute
{
    public const string Setting = "SMARTCV_A38_DEMO_ZIP";
    public A38DemoFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable(Setting)))
            Skip = $"Set {Setting} to the A36_A42_B38_B42 demo ZIP.";
    }
}
