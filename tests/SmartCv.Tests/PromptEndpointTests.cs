using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

public class PromptEndpointTests
{
    private static WebApplicationFactory<Program> Factory(string environment = "Development", bool enabled = true) =>
        TestSupport.ApiFactory(environment, new()
        {
            ["CvUpload:Endpoint:Enabled"] = enabled.ToString(), ["Scoring:WorkerEnabled"] = "false"
        }).WithWebHostBuilder(host => host.ConfigureLogging(logging => logging.ClearProviders()));

    [Fact]
    public async Task Empty_list_and_missing_details_or_active_return_expected_status()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        Assert.Empty((await client.GetFromJsonAsync<List<PromptVersionDto>>("/api/prompts"))!);
        foreach (var path in new[] { "/api/prompts/active", "/api/prompts/99999" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/prompts/99999/activate", null)).StatusCode);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(101, 1, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(1, 20001, 0)]
    [InlineData(1, 1, 501)]
    public async Task Invalid_lengths_return_400_without_inserting(int nameLength, int promptLength, int noteLength)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/prompts", new CreatePromptVersionRequest
        {
            VersionName = new string('v', nameLength), SystemPrompt = new string('p', promptLength),
            ChangeNote = new string('n', noteLength)
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<SmartCvDbContext>().PromptVersions.ToListAsync());
    }

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(100, 20000, 500)]
    public async Task Valid_boundaries_save_inactive_metadata_and_return_location(int nameLength, int promptLength, int noteLength)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var request = new CreatePromptVersionRequest
        {
            VersionName = new string('v', nameLength), SystemPrompt = new string('p', promptLength),
            ChangeNote = noteLength == 0 ? null : new string('n', noteLength)
        };
        using var response = await client.PostAsJsonAsync("/api/prompts", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<PromptVersionDto>())!;
        Assert.False(dto.IsActive);
        Assert.False(dto.HasRuns);
        Assert.Null(dto.LatestAccuracyPercent);
        Assert.Equal(request.ChangeNote, dto.ChangeNote);
        Assert.Equal(DateTimeKind.Utc, dto.CreatedAt.Kind);
        Assert.Equal($"/api/prompts/{dto.PromptVersionId}", response.Headers.Location!.OriginalString);
        var read = (await client.GetFromJsonAsync<PromptVersionDto>(response.Headers.Location))!;
        Assert.Equal(request.SystemPrompt, read.SystemPrompt);
    }

    [Fact]
    public async Task Duplicate_names_or_whitespace_equivalent_content_are_rejected_without_changing_old_version()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var original = await client.PostAsJsonAsync("/api/prompts", new CreatePromptVersionRequest
        {
            VersionName = "  v1  ", SystemPrompt = " Đánh giá kỹ năng \n và kinh nghiệm. ", ChangeNote = " Ghi chú "
        });
        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        var before = (await original.Content.ReadFromJsonAsync<PromptVersionDto>())!;
        Assert.Equal("v1", before.VersionName);
        Assert.Equal("Ghi chú", before.ChangeNote);
        using var duplicateName = await client.PostAsJsonAsync("/api/prompts", new CreatePromptVersionRequest
        {
            VersionName = "v1", SystemPrompt = "Other content"
        });
        using var duplicateContent = await client.PostAsJsonAsync("/api/prompts", new CreatePromptVersionRequest
        {
            VersionName = "v2", SystemPrompt = "Đánh giá\tkỹ năng\u00a0và kinh nghiệm."
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicateName.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicateContent.StatusCode);
        var after = (await client.GetFromJsonAsync<PromptVersionDto>($"/api/prompts/{before.PromptVersionId}"))!;
        Assert.Equal(before.SystemPrompt, after.SystemPrompt);
        Assert.Equal(before.ChangeNote, after.ChangeNote);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Single((await client.GetFromJsonAsync<List<PromptVersionDto>>("/api/prompts"))!);
    }

    [Fact]
    public async Task List_is_stable_newest_first_and_active_is_exact()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var time = DateTime.UtcNow;
        int activeId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            var active = new PromptVersion { VersionName = "active", SystemPrompt = "original", IsActive = true, CreatedAt = time.AddDays(-1) };
            db.AddRange(active,
                new PromptVersion { VersionName = "new1", SystemPrompt = "one", CreatedAt = time },
                new PromptVersion { VersionName = "new2", SystemPrompt = "two", CreatedAt = time });
            await db.SaveChangesAsync();
            activeId = active.PromptVersionId;
        }
        var list = (await client.GetFromJsonAsync<List<PromptVersionDto>>("/api/prompts"))!;
        Assert.Equal(new[] { "new2", "new1", "active" }, list.Select(p => p.VersionName));
        Assert.Equal(activeId, (await client.GetFromJsonAsync<PromptVersionDto>("/api/prompts/active"))!.PromptVersionId);
    }

    [Fact]
    public async Task Database_failure_is_not_misreported_as_duplicate_name()
    {
        using var factory = Factory().WithWebHostBuilder(host => host.ConfigureTestServices(services =>
            services.AddDbContext<SmartCvDbContext>(options => options.AddInterceptors(new SaveFault()))));
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/prompts", new CreatePromptVersionRequest
        {
            VersionName = "v1", SystemPrompt = "valid content"
        });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", false)]
    public async Task Prompt_management_uses_existing_development_access_boundary(string environment, bool enabled)
    {
        using var factory = Factory(environment, enabled);
        using var client = factory.CreateClient();
        foreach (var path in new[] { "/api/prompts", "/api/prompts/active", "/api/prompts/1" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/prompts", new CreatePromptVersionRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/prompts/1/activate", null)).StatusCode);
    }

    private sealed class SaveFault : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("Synthetic database outage; not a duplicate key.");
    }
}
