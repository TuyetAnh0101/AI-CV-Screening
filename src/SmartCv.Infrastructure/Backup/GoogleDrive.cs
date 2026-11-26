using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartCv.Core.Backup;

namespace SmartCv.Infrastructure.Backup;

public sealed record CloudFile(string Id, string Name, long Size, string? Md5, IReadOnlyDictionary<string, string>? Properties);

/// <summary>Where encrypted backups live. Google Drive in production, in-memory in tests.</summary>
public interface IBackupCloudStore
{
    Task<CloudFile> UploadAsync(string name, Stream content, long length, IReadOnlyDictionary<string, string> properties, CancellationToken ct);
    Task DownloadAsync(string fileId, Stream destination, CancellationToken ct);
    /// <summary>Null when the file no longer exists or is in the trash.</summary>
    Task<CloudFile?> GetAsync(string fileId, CancellationToken ct);
    /// <summary>Idempotent: an already missing file is not an error.</summary>
    Task DeleteAsync(string fileId, CancellationToken ct);
    Task<IReadOnlyList<CloudFile>> ListAsync(string nameContains, CancellationToken ct);
}

public sealed record GoogleCredential(string ClientId, string ClientSecret, string RefreshToken);
public sealed record GoogleAccount(string? Email, long? QuotaUsed, long? QuotaLimit);

/// <summary>Minimal Google OAuth 2.0 + Drive v3 REST client. Only the <c>drive.file</c> scope: the app sees nothing it did not create.</summary>
public static class GoogleDriveApi
{
    public const string Scope = "https://www.googleapis.com/auth/drive.file";
    public const string FolderMime = "application/vnd.google-apps.folder";
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    internal const string Files = "https://www.googleapis.com/drive/v3/files";
    internal const string Upload = "https://www.googleapis.com/upload/drive/v3/files";
    private const string About = "https://www.googleapis.com/drive/v3/about";

    public static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        return (verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    public static string AuthorizationUrl(string clientId, string redirectUri, string state, string challenge) =>
        AuthEndpoint + "?" + string.Join("&", new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["redirect_uri"] = redirectUri, ["response_type"] = "code", ["scope"] = Scope,
            // offline + consent: Google returns a refresh token so the worker runs without anyone signed in.
            ["access_type"] = "offline", ["prompt"] = "consent", ["include_granted_scopes"] = "false",
            ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256"
        }.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));

    public static async Task<(string AccessToken, string? RefreshToken, string? Scope)> ExchangeCodeAsync(HttpClient http, string clientId, string clientSecret,
        string code, string redirectUri, string verifier, CancellationToken ct)
    {
        var token = await TokenAsync(http, new()
        {
            ["client_id"] = clientId, ["client_secret"] = clientSecret, ["code"] = code, ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri, ["grant_type"] = "authorization_code"
        }, ct);
        return (token.AccessToken!, token.RefreshToken, token.Scope);
    }

    public static async Task<(string AccessToken, DateTimeOffset ExpiresAt)> RefreshAsync(HttpClient http, GoogleCredential credential, CancellationToken ct)
    {
        var token = await TokenAsync(http, new()
        {
            ["client_id"] = credential.ClientId, ["client_secret"] = credential.ClientSecret,
            ["refresh_token"] = credential.RefreshToken, ["grant_type"] = "refresh_token"
        }, ct);
        return (token.AccessToken!, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 60)));
    }

    /// <summary>Best effort: a failed revocation never keeps the local credential.</summary>
    public static async Task RevokeAsync(HttpClient http, string refreshToken, CancellationToken ct)
    {
        try { using var _ = await http.PostAsync(RevokeEndpoint, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken }), ct); }
        catch (HttpRequestException) { }
    }

    public static async Task<GoogleAccount> AboutAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, About + "?fields=user(emailAddress),storageQuota(limit,usage)");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await Send(http, request, ct);
        var about = await response.Content.ReadFromJsonAsync<AboutDocument>(ct);
        return new(about?.User?.EmailAddress, Long(about?.StorageQuota?.Usage), Long(about?.StorageQuota?.Limit));
    }

    /// <summary>Finds the app's folder (only app-created folders are visible under drive.file) or creates it.</summary>
    public static async Task<string> EnsureFolderAsync(HttpClient http, string accessToken, string name, CancellationToken ct)
    {
        var query = $"name='{name.Replace("\\", "\\\\").Replace("'", "\\'")}' and mimeType='{FolderMime}' and trashed=false";
        using (var list = new HttpRequestMessage(HttpMethod.Get, Files + "?q=" + Uri.EscapeDataString(query) + "&fields=files(id)&spaces=drive"))
        {
            list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await Send(http, list, ct);
            var found = await response.Content.ReadFromJsonAsync<FileList>(ct);
            if (found?.Files is { Count: > 0 } files) return files[0].Id!;
        }
        using var create = new HttpRequestMessage(HttpMethod.Post, Files + "?fields=id")
        { Content = JsonContent.Create(new { name, mimeType = FolderMime, appProperties = new Dictionary<string, string> { ["scapp"] = "backup-root" } }) };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var created = await Send(http, create, ct);
        return (await created.Content.ReadFromJsonAsync<DriveFile>(ct))?.Id ?? throw new BackupException(BackupErrors.DriveUnavailable);
    }

    private static async Task<TokenDocument> TokenAsync(HttpClient http, Dictionary<string, string> form, CancellationToken ct)
    {
        HttpResponseMessage response;
        try { response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct); }
        catch (HttpRequestException) { throw new BackupException(BackupErrors.DriveUnavailable); }
        using (response)
        {
            // invalid_grant/invalid_client: revoked, expired or wrong client. Only reconnecting helps.
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized) throw new BackupException(BackupErrors.DriveAuth);
            if (!response.IsSuccessStatusCode) throw new BackupException(BackupErrors.DriveUnavailable);
            var token = await response.Content.ReadFromJsonAsync<TokenDocument>(ct);
            if (string.IsNullOrEmpty(token?.AccessToken)) throw new BackupException(BackupErrors.DriveAuth);
            return token;
        }
    }

    /// <summary>Maps Drive failures to stable codes. Caller disposes the response.</summary>
    internal static async Task<HttpResponseMessage> Send(HttpClient http, HttpRequestMessage request, CancellationToken ct, bool allowNotFound = false)
    {
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException) { throw new BackupException(BackupErrors.DriveUnavailable); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new BackupException(BackupErrors.DriveUnavailable); }
        if (response.IsSuccessStatusCode || (int)response.StatusCode == 308 || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)) return response;
        var body = response.Content is null ? "" : await response.Content.ReadAsStringAsync(ct);
        response.Dispose();
        throw new BackupException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => BackupErrors.DriveAuth,
            HttpStatusCode.NotFound => BackupErrors.DriveMissing,
            HttpStatusCode.Forbidden when body.Contains("storageQuotaExceeded", StringComparison.Ordinal) => BackupErrors.DriveQuota,
            HttpStatusCode.Forbidden when body.Contains("insufficient", StringComparison.OrdinalIgnoreCase) => BackupErrors.DriveAuth,
            _ => BackupErrors.DriveUnavailable
        });
    }

    private static long? Long(string? value) => long.TryParse(value, out var number) ? number : null;
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal sealed record TokenDocument([property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken, [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope);
    internal sealed record AboutDocument(AboutUser? User, AboutQuota? StorageQuota);
    internal sealed record AboutUser(string? EmailAddress);
    internal sealed record AboutQuota(string? Limit, string? Usage);
    internal sealed record DriveFile(string? Id, string? Name, string? Size, string? Md5Checksum, bool? Trashed, Dictionary<string, string>? AppProperties);
    internal sealed record FileList(List<DriveFile>? Files, string? NextPageToken);
}

/// <summary>Drive store bound to one folder. Uploads are resumable (8 MiB chunks) and verified against Drive's MD5.</summary>
public sealed class GoogleDriveStore(HttpClient http, GoogleCredential credential, string folderId) : IBackupCloudStore
{
    private const int UploadChunk = 8 * 1024 * 1024; // multiple of 256 KiB as Drive requires
    private const string FileFields = "id,name,size,md5Checksum,trashed,appProperties";
    private string? accessToken;
    private DateTimeOffset expiresAt;

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (accessToken is null || DateTimeOffset.UtcNow >= expiresAt)
            (accessToken, expiresAt) = await GoogleDriveApi.RefreshAsync(http, credential, ct);
        return accessToken;
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, CancellationToken ct, bool allowNotFound = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = create();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
            try { return await GoogleDriveApi.Send(http, request, ct, allowNotFound); }
            catch (BackupException error) when (error.Code == BackupErrors.DriveAuth && attempt == 0) { accessToken = null; }
            catch (BackupException error) when (error.Code == BackupErrors.DriveUnavailable && attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct); }
        }
    }

    /// <summary>
    /// A failed chunk restarts the whole upload from a fresh session: re-sending a partially received chunk is not
    /// a supported resume, and staged encrypted files are seekable.
    /// </summary>
    public async Task<CloudFile> UploadAsync(string name, Stream content, long length, IReadOnlyDictionary<string, string> properties, CancellationToken ct)
    {
        if (length <= 0 || !content.CanSeek) throw new BackupException(BackupErrors.Consistency);
        for (var attempt = 0; ; attempt++)
        {
            content.Position = 0;
            try { return await UploadOnceAsync(name, content, length, properties, ct); }
            catch (BackupException error) when (error.Code == BackupErrors.DriveUnavailable && attempt < 2)
            { await Task.Delay(TimeSpan.FromSeconds(5 << attempt), ct); }
        }
    }

    private async Task<CloudFile> UploadOnceAsync(string name, Stream content, long length, IReadOnlyDictionary<string, string> properties, CancellationToken ct)
    {
        using var start = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, GoogleDriveApi.Upload + "?uploadType=resumable&fields=" + FileFields)
            { Content = JsonContent.Create(new { name, parents = new[] { folderId }, appProperties = properties }) };
            request.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
            request.Headers.Add("X-Upload-Content-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return request;
        }, ct);
        var session = start.Headers.Location ?? throw new BackupException(BackupErrors.DriveUnavailable);
        var buffer = new byte[UploadChunk];
        long offset = 0;
        while (true)
        {
            var count = 0;
            while (count < buffer.Length) { var read = await content.ReadAsync(buffer.AsMemory(count), ct); if (read == 0) break; count += read; }
            var last = offset + count >= length;
            if (count == 0 || (!last && count < buffer.Length) || offset + count > length) throw new BackupException(BackupErrors.Consistency);
            using var request = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent(buffer, 0, count) };
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, length);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
            using var response = await GoogleDriveApi.Send(http, request, ct);
            offset += count;
            if ((int)response.StatusCode == 308) { if (last) throw new BackupException(BackupErrors.DriveVerify); continue; }
            var file = await response.Content.ReadFromJsonAsync<GoogleDriveApi.DriveFile>(ct);
            return Map(file) ?? throw new BackupException(BackupErrors.DriveUnavailable);
        }
    }

    public async Task DownloadAsync(string fileId, Stream destination, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, GoogleDriveApi.Files + "/" + Uri.EscapeDataString(fileId) + "?alt=media"), ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        try { await stream.CopyToAsync(destination, ct); }
        catch (IOException) when (!ct.IsCancellationRequested) { throw new BackupException(BackupErrors.DriveUnavailable); }
    }

    public async Task<CloudFile?> GetAsync(string fileId, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            GoogleDriveApi.Files + "/" + Uri.EscapeDataString(fileId) + "?fields=" + FileFields), ct, allowNotFound: true);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var file = await response.Content.ReadFromJsonAsync<GoogleDriveApi.DriveFile>(ct);
        return file?.Trashed == true ? null : Map(file);
    }

    public async Task DeleteAsync(string fileId, CancellationToken ct)
    {
        using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, GoogleDriveApi.Files + "/" + Uri.EscapeDataString(fileId)), ct, allowNotFound: true);
    }

    public async Task<IReadOnlyList<CloudFile>> ListAsync(string nameContains, CancellationToken ct)
    {
        var result = new List<CloudFile>();
        string? page = null;
        // Drive's "name contains" only matches name PREFIXES, so a suffix such as ".manifest.enc" would find
        // nothing and a catalog rebuild would silently report an empty Drive. List the folder and filter here.
        var query = $"'{folderId}' in parents and trashed=false";
        do
        {
            var url = GoogleDriveApi.Files + "?q=" + Uri.EscapeDataString(query) + "&pageSize=1000&spaces=drive&fields=nextPageToken,files(" + FileFields + ")"
                + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
            var list = await response.Content.ReadFromJsonAsync<GoogleDriveApi.FileList>(ct);
            result.AddRange((list?.Files ?? []).Select(Map).OfType<CloudFile>());
            page = list?.NextPageToken;
        } while (page is not null);
        result.RemoveAll(f => !f.Name.Contains(nameContains, StringComparison.Ordinal));
        return result;
    }

    private static CloudFile? Map(GoogleDriveApi.DriveFile? file) => file?.Id is null ? null
        : new(file.Id, file.Name ?? "", long.TryParse(file.Size, out var size) ? size : 0, file.Md5Checksum, file.AppProperties);
}
