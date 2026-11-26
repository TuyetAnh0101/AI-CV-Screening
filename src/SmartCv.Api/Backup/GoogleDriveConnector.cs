using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SmartCv.Core.Backup;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Backup;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Backup;

public sealed record DriveOAuthSettings(string ClientId, string ClientSecret, string? RedirectUri, string FolderName);

/// <summary>
/// Admin-initiated OAuth (authorization code + PKCE, scope drive.file). The state is encrypted, bound to the
/// Admin who started it, valid for ten minutes and single use. Tokens are stored only as Data Protection ciphertext.
/// </summary>
public sealed class GoogleDriveConnector(IConfiguration config, IDataProtectionProvider protection, IMemoryCache cache, IHttpClientFactory http,
    BackupDriveCredentials credentials, SmartCvDbContext db, TimeProvider clock)
{
    private const string Purpose = "SmartCv.Backup.GoogleDrive.OAuthState.v1";
    public const string CallbackPath = "/api/admin/backups/drive/callback";

    /// <summary>Drive keys, falling back to the OAuth client already used for Google sign-in.</summary>
    public DriveOAuthSettings? Settings()
    {
        var id = config["Backup:GoogleDrive:ClientId"];
        var secret = config["Backup:GoogleDrive:ClientSecret"];
        if (string.IsNullOrWhiteSpace(id)) { id = config["Authentication:Google:ClientId"]; secret = config["Authentication:Google:ClientSecret"]; }
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret)) return null;
        var folder = config["Backup:GoogleDrive:FolderName"];
        return new(id, secret, config["Backup:GoogleDrive:RedirectUri"], string.IsNullOrWhiteSpace(folder) ? "SCApp Backups" : folder);
    }

    public string RedirectUri(HttpRequest request) => Settings()?.RedirectUri is { Length: > 0 } configured ? configured
        : $"{request.Scheme}://{request.Host}{request.PathBase}{CallbackPath}";

    private sealed record State(string Nonce, int UserId, string Verifier, DateTime ExpiresUtc, string RedirectUri);

    public string Start(int userId, HttpRequest request)
    {
        var settings = Settings() ?? throw new BackupException(BackupErrors.Config);
        var (verifier, challenge) = GoogleDriveApi.Pkce();
        var state = new State(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), userId, verifier, clock.GetUtcNow().UtcDateTime.AddMinutes(10), RedirectUri(request));
        var sealedState = protection.CreateProtector(Purpose).Protect(JsonSerializer.Serialize(state));
        return GoogleDriveApi.AuthorizationUrl(settings.ClientId, state.RedirectUri, sealedState, challenge);
    }

    /// <summary>Returns the Admin's user id on success, otherwise a safe reason code.</summary>
    public async Task<(int? UserId, string? Error)> CompleteAsync(string? code, string? state, CancellationToken ct)
    {
        var settings = Settings();
        if (settings is null || string.IsNullOrEmpty(state)) return (null, "invalid_state");
        State parsed;
        try { parsed = JsonSerializer.Deserialize<State>(protection.CreateProtector(Purpose).Unprotect(state))!; }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException) { return (null, "invalid_state"); }
        var key = "backup-oauth:" + parsed.Nonce;
        if (parsed.ExpiresUtc < clock.GetUtcNow().UtcDateTime || cache.TryGetValue(key, out _)) return (null, "expired_state");
        cache.Set(key, true, TimeSpan.FromMinutes(15));
        // The initiating account must still be an active Admin when Google calls back.
        if (!await db.Users.AnyAsync(u => u.UserId == parsed.UserId && u.IsActive && u.Role == "Admin", ct)) return (null, "forbidden");
        if (string.IsNullOrEmpty(code)) return (parsed.UserId, "access_denied");
        try
        {
            var client = http.CreateClient(GoogleDriveStoreFactory.HttpClientName);
            var token = await GoogleDriveApi.ExchangeCodeAsync(client, settings.ClientId, settings.ClientSecret, code, parsed.RedirectUri, parsed.Verifier, ct);
            if (string.IsNullOrEmpty(token.RefreshToken)) return (parsed.UserId, "no_refresh_token");
            if (token.Scope is { } scope && !scope.Split(' ').Contains(GoogleDriveApi.Scope)) return (parsed.UserId, "scope_denied");
            var about = await GoogleDriveApi.AboutAsync(client, token.AccessToken, ct);
            var folderId = await GoogleDriveApi.EnsureFolderAsync(client, token.AccessToken, settings.FolderName, ct);
            var now = clock.GetUtcNow().UtcDateTime;
            var connection = await db.BackupDriveConnections.SingleOrDefaultAsync(ct);
            if (connection is null) db.BackupDriveConnections.Add(connection = new BackupDriveConnection { BackupDriveConnectionId = 1 });
            connection.ProtectedCredential = credentials.Protect(new(settings.ClientId, settings.ClientSecret, token.RefreshToken));
            connection.AccountEmail = about.Email; connection.FolderId = folderId; connection.FolderName = settings.FolderName;
            connection.ConnectedAtUtc = now; connection.ConnectedByUserId = parsed.UserId; connection.LastCheckedAtUtc = now;
            connection.LastCheckOk = true; connection.LastCheckError = null; connection.QuotaUsedBytes = about.QuotaUsed; connection.QuotaLimitBytes = about.QuotaLimit;
            await db.SaveChangesAsync(ct);
            return (parsed.UserId, null);
        }
        catch (BackupException error) { return (parsed.UserId, error.Code.ToLowerInvariant()); }
    }

    public async Task<(bool Ok, string? Error)> CheckAsync(CancellationToken ct)
    {
        var connection = await db.BackupDriveConnections.SingleOrDefaultAsync(ct);
        if (connection is null) return (false, BackupErrors.DriveNotConnected);
        string? error = null;
        try
        {
            var client = http.CreateClient(GoogleDriveStoreFactory.HttpClientName);
            var credential = credentials.Unprotect(connection.ProtectedCredential);
            var (token, _) = await GoogleDriveApi.RefreshAsync(client, credential, ct);
            var about = await GoogleDriveApi.AboutAsync(client, token, ct);
            var store = new GoogleDriveStore(client, credential, connection.FolderId);
            if (await store.GetAsync(connection.FolderId, ct) is null) error = BackupErrors.DriveMissing;
            connection.QuotaUsedBytes = about.QuotaUsed; connection.QuotaLimitBytes = about.QuotaLimit; connection.AccountEmail = about.Email;
        }
        catch (BackupException failure) { error = failure.Code; }
        connection.LastCheckedAtUtc = clock.GetUtcNow().UtcDateTime; connection.LastCheckOk = error is null; connection.LastCheckError = error;
        await db.SaveChangesAsync(ct);
        return (error is null, error);
    }

    /// <summary>Revokes the token and forgets it. Backups already on Drive stay there.</summary>
    public async Task<bool> DisconnectAsync(CancellationToken ct)
    {
        var connection = await db.BackupDriveConnections.SingleOrDefaultAsync(ct);
        if (connection is null) return false;
        try { await GoogleDriveApi.RevokeAsync(http.CreateClient(GoogleDriveStoreFactory.HttpClientName), credentials.Unprotect(connection.ProtectedCredential).RefreshToken, ct); }
        catch (BackupException) { }
        db.BackupDriveConnections.Remove(connection);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
