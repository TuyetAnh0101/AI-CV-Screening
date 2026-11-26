using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SmartCv.Core.Backup;
using SmartCv.Infrastructure.Backup;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Ops;

public sealed class A62SqlFact : FactAttribute
{
    public const string Setting = "SMARTCV_A62_TEST_SQL_CONNECTION";
    public A62SqlFact()
    {
        var value = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrWhiteSpace(value)) { Skip = "NOT RUN: opt in with SMARTCV_A62_TEST_SQL_CONNECTION; creates only new A62 synthetic catalogs."; return; }
        try
        {
            var builder = new SqlConnectionStringBuilder(value);
            if (builder.InitialCatalog.Equals("SmartCvDb", StringComparison.OrdinalIgnoreCase) || builder.AttachDBFilename.Length > 0 || builder.DataSource.Length == 0) throw new ArgumentException();
        }
        catch (ArgumentException) { Skip = "NOT RUN: unsafe SQL test configuration."; }
    }
}

/// <summary>Drive stand-in: same contract (size, MD5, missing = null, idempotent delete), kept in memory.</summary>
public sealed class MemoryCloudStore : IBackupCloudStore, IBackupCloudStoreFactory
{
    public sealed record Entry(string Name, byte[] Content, Dictionary<string, string> Properties);
    public ConcurrentDictionary<string, Entry> Files { get; } = new();
    public Func<string, string?>? FailUpload { get; set; }
    public int Uploads;

    public Task<IBackupCloudStore> OpenAsync(SmartCvDbContext db, CancellationToken ct) => Task.FromResult<IBackupCloudStore>(this);

    public async Task<CloudFile> UploadAsync(string name, Stream content, long length, IReadOnlyDictionary<string, string> properties, CancellationToken ct)
    {
        if (FailUpload?.Invoke(name) is { } code) throw new BackupException(code);
        using var memory = new MemoryStream();
        await content.CopyToAsync(memory, ct);
        var bytes = memory.ToArray();
        Assert.Equal(length, bytes.Length);
        var id = "f" + Guid.NewGuid().ToString("N");
        Files[id] = new(name, bytes, new(properties));
        Interlocked.Increment(ref Uploads);
        return Describe(id);
    }

    private CloudFile Describe(string id)
    {
        var entry = Files[id];
        return new(id, entry.Name, entry.Content.Length, Convert.ToHexStringLower(MD5.HashData(entry.Content)), entry.Properties);
    }

    public async Task DownloadAsync(string fileId, Stream destination, CancellationToken ct)
    {
        if (!Files.TryGetValue(fileId, out var entry)) throw new BackupException(BackupErrors.DriveMissing);
        await destination.WriteAsync(entry.Content, ct);
    }
    public Task<CloudFile?> GetAsync(string fileId, CancellationToken ct) => Task.FromResult(Files.ContainsKey(fileId) ? Describe(fileId) : null);
    public Task DeleteAsync(string fileId, CancellationToken ct) { Files.TryRemove(fileId, out _); return Task.CompletedTask; }
    public Task<IReadOnlyList<CloudFile>> ListAsync(string nameContains, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CloudFile>>(Files.Where(f => f.Value.Name.Contains(nameContains, StringComparison.Ordinal)).Select(f => Describe(f.Key)).ToList());
}

/// <summary>Fake Google endpoints: OAuth token/revoke, Drive about/files/resumable upload/download/delete.</summary>
public sealed class FakeGoogleHandler : HttpMessageHandler
{
    public string RefreshToken { get; } = "fake-refresh-" + Guid.NewGuid().ToString("N");
    public string Email { get; set; } = "backup.owner@example.test";
    public bool RevokedAccess { get; set; }
    public bool QuotaExceeded { get; set; }
    public ConcurrentDictionary<string, (string Name, byte[] Content, string Mime, string Parent, bool Trashed)> Files { get; } = new();
    public List<string> Requests { get; } = [];
    private readonly ConcurrentDictionary<string, (string Name, string Parent, long Length, MemoryStream Buffer)> sessions = new();
    public string? LastCodeVerifier;

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(body) };
    private object Describe(string id)
    {
        var f = Files[id];
        return new { id, name = f.Name, size = f.Content.Length.ToString(), md5Checksum = Convert.ToHexStringLower(MD5.HashData(f.Content)), trashed = f.Trashed };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!;
        lock (Requests) Requests.Add(request.Method + " " + url.AbsolutePath);
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        if (url.Host == "oauth2.googleapis.com" && url.AbsolutePath == "/token")
        {
            var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
            if (form["grant_type"] == "authorization_code")
            {
                LastCodeVerifier = form["code_verifier"];
                if (form["code"] != "good-code" || string.IsNullOrEmpty(LastCodeVerifier)) return Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
                return Json(new { access_token = "access", refresh_token = RefreshToken, expires_in = 3600, scope = GoogleDriveApi.Scope });
            }
            if (RevokedAccess || form["refresh_token"] != RefreshToken) return Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
            return Json(new { access_token = "access", expires_in = 3600 });
        }
        if (url.Host == "oauth2.googleapis.com" && url.AbsolutePath == "/revoke") { RevokedAccess = true; return Json(new { }); }
        if (request.Headers.Authorization?.Parameter != "access") return Json(new { }, HttpStatusCode.Unauthorized);
        if (url.AbsolutePath == "/drive/v3/about") return Json(new { user = new { emailAddress = Email }, storageQuota = new { usage = "1000", limit = "15000000000" } });
        if (url.AbsolutePath == "/drive/v3/files" && request.Method == HttpMethod.Get)
        {
            var q = query["q"] ?? "";
            var folders = q.Contains(GoogleDriveApi.FolderMime) ? Files.Where(f => f.Value.Mime == GoogleDriveApi.FolderMime && !f.Value.Trashed) : Files.Where(f => f.Value.Mime != GoogleDriveApi.FolderMime && !f.Value.Trashed);
            // As documented by Google: "name contains" matches name prefixes only, never a suffix.
            var contains = System.Text.RegularExpressions.Regex.Match(q, "name contains '([^']*)'");
            if (contains.Success) folders = folders.Where(f => f.Value.Name.StartsWith(contains.Groups[1].Value, StringComparison.Ordinal));
            var page = int.Parse(query["pageToken"] ?? "0");
            var all = folders.Select(f => f.Key).Order().ToList();
            var slice = all.Skip(page).Take(2).Select(Describe).ToList();
            return Json(new { files = slice, nextPageToken = page + 2 < all.Count ? (page + 2).ToString() : null });
        }
        if (url.AbsolutePath == "/drive/v3/files" && request.Method == HttpMethod.Post)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            var id = "folder" + Guid.NewGuid().ToString("N");
            Files[id] = (body.GetProperty("name").GetString()!, [], body.GetProperty("mimeType").GetString()!, "root", false);
            return Json(new { id });
        }
        if (url.AbsolutePath == "/upload/drive/v3/files" && request.Method == HttpMethod.Post)
        {
            if (QuotaExceeded) return new(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":{\"errors\":[{\"reason\":\"storageQuotaExceeded\"}]}}") };
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            var session = Guid.NewGuid().ToString("N");
            sessions[session] = (body.GetProperty("name").GetString()!, body.GetProperty("parents")[0].GetString()!,
                long.Parse(request.Headers.GetValues("X-Upload-Content-Length").Single()), new MemoryStream());
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
            response.Headers.Location = new Uri("https://www.googleapis.com/upload/drive/v3/session/" + session);
            return response;
        }
        if (url.AbsolutePath.StartsWith("/upload/drive/v3/session/"))
        {
            var session = url.Segments[^1];
            var s = sessions[session];
            var range = request.Content!.Headers.ContentRange!;
            Assert.Equal(s.Buffer.Length, range.From);
            var chunk = await request.Content.ReadAsByteArrayAsync(ct);
            Assert.Equal(range.To - range.From + 1, chunk.Length);
            s.Buffer.Write(chunk);
            if (s.Buffer.Length < s.Length) return new((HttpStatusCode)308) { Content = new StringContent("") };
            var id = "file" + Guid.NewGuid().ToString("N");
            Files[id] = (s.Name, s.Buffer.ToArray(), "application/octet-stream", s.Parent, false);
            return Json(Describe(id));
        }
        var fileId = url.Segments[^1];
        if (url.AbsolutePath.StartsWith("/drive/v3/files/"))
        {
            if (!Files.TryGetValue(fileId, out var file)) return Json(new { }, HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Delete) { Files.TryRemove(fileId, out _); return new(HttpStatusCode.NoContent); }
            return query["alt"] == "media" ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Content) } : Json(Describe(fileId));
        }
        return Json(new { }, HttpStatusCode.NotFound);
    }
}
