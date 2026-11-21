using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Uploads;

namespace SmartCv.Tests;

public class WorkspaceDuplicateLeakTests
{
    [Fact]
    public async Task Same_workspace_job_duplicate_returns_workspace_specific_message()
    {
        using var fixture = await WorkspaceIsolationFixture.CreateAsync();
        using var client = fixture.Client(2);
        using var response = await client.PostAsJsonAsync("/api/jobs", new { title = "A52 job 2" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Đã có vị trí cùng tên trong không gian làm việc của bạn.", await response.Content.ReadAsStringAsync());
        await fixture.WithDb(2, async db => Assert.Equal(1, await db.Jobs.CountAsync()));
    }
    [Fact]
    public async Task Upload_hash_matches_only_within_current_workspace_and_storage_is_independent()
    {
        using var fixture = await WorkspaceIsolationFixture.CreateAsync();
        var bytes = TestSupport.Pdf("A53 entirely synthetic cross-workspace duplicate");
        using var stream = new MemoryStream(bytes);
        var hash = await FileHasher.ComputeSha256Async(stream);
        var foreignId = Guid.NewGuid();
        await fixture.WithDb(1, async db =>
        {
            db.Add(new Cv { CvId = foreignId, FileHash = hash, OriginalFileName = "FOREIGN-SECRET.pdf", StoredPath = "synthetic/foreign.pdf", ContentType = "application/pdf", FileSizeBytes = bytes.Length, UploadedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });
        using var client = fixture.Client(2);
        async Task<CvUploadResponse> Upload()
        {
            using var content = new MultipartFormDataContent { { new ByteArrayContent(bytes), "files", "mine.pdf" }, { new StringContent("true"), "consentConfirmed" }, { new StringContent("Email"), "sourceChannel" } };
            using var response = await client.PostAsync("/api/cvs/upload", content);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("FOREIGN-SECRET", body); Assert.DoesNotContain(foreignId.ToString(), body);
            return (await response.Content.ReadFromJsonAsync<CvUploadResponse>())!;
        }
        var first = Assert.Single((await Upload()).Files);
        Assert.Equal(CvUploadStatuses.Accepted, first.Status);
        Assert.NotNull(first.CvId); Assert.NotEqual(foreignId, first.CvId);
        var second = Assert.Single((await Upload()).Files);
        Assert.Equal(CvUploadErrorCodes.Duplicate, second.ErrorCode);
        Assert.Equal(first.CvId, second.ExistingCvId);
        string? foreignPath = null;
        await fixture.WithDb(1, async db => foreignPath = (await db.Cvs.SingleAsync(c => c.CvId == foreignId)).StoredPath);
        await fixture.WithDb(2, async db => Assert.NotEqual(foreignPath, (await db.Cvs.SingleAsync(c => c.CvId == first.CvId)).StoredPath));
    }

    [Fact]
    public async Task Job_title_in_another_workspace_does_not_block_creation()
    {
        using var fixture = await WorkspaceIsolationFixture.CreateAsync();
        using var client = fixture.Client(2);
        using var response = await client.PostAsJsonAsync("/api/jobs", new { title = "A52 job 1" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("A52 job 1", await response.Content.ReadAsStringAsync());
        await fixture.WithDb(2, async db => Assert.Equal(2, await db.Jobs.CountAsync()));
        await fixture.WithDb(1, async db => Assert.Equal(1, await db.Jobs.CountAsync()));
    }

    [Fact]
    public async Task Candidate_duplicate_response_only_identifies_current_workspace_matches()
    {
        using var fixture = await WorkspaceIsolationFixture.CreateAsync();
        using var client = fixture.Client(2);
        using var response = await client.PostAsJsonAsync("/api/candidates", new { fullName = "New synthetic", email = "shared@example.invalid", phoneNumber = "0900000000" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("A52 candidate 2", body);
        Assert.DoesNotContain("A52 candidate 1", body);
        await fixture.WithDb(2, async db => Assert.Equal(1, await db.Candidates.CountAsync()));
    }
}
