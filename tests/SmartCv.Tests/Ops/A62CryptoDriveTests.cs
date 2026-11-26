using System.Security.Cryptography;
using SmartCv.Core.Backup;
using SmartCv.Infrastructure.Backup;

namespace SmartCv.Tests.Ops;

public sealed class A62CryptoTests
{
    private static BackupKey Key() => new(RandomNumberGenerator.GetBytes(32));

    private static async Task<(byte[] Cipher, BackupCipherResult Result)> Encrypt(BackupKey key, byte[] plain)
    {
        using var output = new MemoryStream();
        var result = await BackupCrypto.EncryptAsync(key, new MemoryStream(plain), output, default);
        return (output.ToArray(), result);
    }

    private static async Task<byte[]> Decrypt(BackupKey key, byte[] cipher)
    {
        using var output = new MemoryStream();
        await BackupCrypto.DecryptAsync(key, BackupCrypto.Readable(new MemoryStream(cipher)), output, default);
        return output.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(BackupCrypto.ChunkSize - 1)]
    [InlineData(BackupCrypto.ChunkSize)]
    [InlineData(BackupCrypto.ChunkSize * 3 + 17)]
    public async Task Round_trips_every_size_and_reports_exact_hashes(int size)
    {
        using var key = Key();
        var plain = RandomNumberGenerator.GetBytes(size);
        var (cipher, result) = await Encrypt(key, plain);
        Assert.Equal(plain, await Decrypt(key, cipher));
        Assert.Equal(size, result.PlainBytes); Assert.Equal(cipher.Length, result.CipherBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(plain)), result.PlainSha256);
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(cipher)), result.CipherMd5);
        if (size > 16) Assert.False(cipher.AsSpan().IndexOf(plain.AsSpan(0, 16)) >= 0, "plaintext visible in ciphertext");
    }

    [Fact]
    public async Task Every_tampering_and_the_wrong_key_are_rejected()
    {
        using var key = Key(); using var other = Key();
        var plain = RandomNumberGenerator.GetBytes(BackupCrypto.ChunkSize + 100);
        var (cipher, _) = await Encrypt(key, plain);
        async Task Rejected(byte[] damaged, string code = BackupErrors.Checksum) =>
            Assert.Equal(code, (await Assert.ThrowsAsync<BackupException>(() => Decrypt(key, damaged))).Code);
        foreach (var position in new[] { 10, 30, BackupCrypto.HeaderLength + 2, BackupCrypto.HeaderLength + 50, cipher.Length - 1 })
        {
            var flipped = (byte[])cipher.Clone(); flipped[position] ^= 1;
            await Assert.ThrowsAsync<BackupException>(() => Decrypt(key, flipped));
        }
        await Rejected(cipher[..^1]);
        // Cutting at a chunk boundary: the first chunk alone must not pass as a complete file.
        await Rejected(cipher[..(BackupCrypto.HeaderLength + 4 + BackupCrypto.ChunkSize + 16)]);
        await Rejected([.. cipher, 0]);
        await Rejected(cipher[..BackupCrypto.HeaderLength]);
        Assert.Equal(BackupErrors.KeyMismatch, (await Assert.ThrowsAsync<BackupException>(() => Decrypt(other, cipher))).Code);
        // Two encryptions of the same bytes never share a salt.
        var (again, _) = await Encrypt(key, plain);
        Assert.NotEqual(cipher.AsSpan(25, 16).ToArray(), again.AsSpan(25, 16).ToArray());
    }

    [Fact]
    public void Object_names_are_keyed_and_the_key_format_round_trips()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        using var key = new BackupKey(bytes); using var other = Key();
        var sha = Convert.ToHexStringLower(SHA256.HashData("cv"u8.ToArray()));
        Assert.Equal(key.ObjectKey(sha), key.ObjectKey(sha.ToUpperInvariant()));
        Assert.NotEqual(sha, key.ObjectKey(sha)); Assert.NotEqual(key.ObjectKey(sha), other.ObjectKey(sha));
        Assert.Equal(bytes, BackupKey.Parse(BackupKey.Format(bytes)));
        Assert.Throws<BackupException>(() => BackupKey.Parse("not a key"));
        Assert.Throws<BackupException>(() => new BackupKey(new byte[16]));
    }

    [Fact]
    public void Dpapi_key_store_never_replaces_a_key()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = TestSupport.TempRoot();
        try
        {
            var provider = new DpapiBackupKeyProvider(directory);
            Assert.Equal(BackupErrors.Key, Assert.Throws<BackupException>(() => provider.Load()).Code);
            var bytes = RandomNumberGenerator.GetBytes(32);
            var id = provider.Create(bytes, escrowConfirmed: true);
            using (var loaded = provider.Load()) Assert.Equal(id, loaded.KeyId);
            Assert.NotNull(provider.Info()!.EscrowConfirmedAtUtc);
            Assert.Throws<BackupException>(() => provider.Create(RandomNumberGenerator.GetBytes(32), true));
            var stored = File.ReadAllBytes(Path.Combine(directory, DpapiBackupKeyProvider.KeyFile));
            Assert.False(stored.AsSpan().IndexOf(bytes) >= 0, "key stored unprotected");
        }
        finally { Directory.Delete(directory, true); }
    }
}

public sealed class A62GoogleDriveClientTests
{
    private static (GoogleDriveStore Store, FakeGoogleHandler Google, HttpClient Http) Create()
    {
        var google = new FakeGoogleHandler();
        var http = new HttpClient(google);
        return (new GoogleDriveStore(http, new("client", "secret", google.RefreshToken), "folder-x"), google, http);
    }

    [Fact]
    public async Task Resumable_upload_download_list_and_delete()
    {
        var (store, google, _) = Create();
        var content = RandomNumberGenerator.GetBytes(8 * 1024 * 1024 * 2 + 12345); // three chunks
        var file = await store.UploadAsync("big.enc", new MemoryStream(content), content.Length, new Dictionary<string, string> { ["scapp"] = "full" }, default);
        Assert.Equal(content.Length, file.Size); Assert.Equal(Convert.ToHexStringLower(MD5.HashData(content)), file.Md5);
        Assert.Equal(3, google.Requests.Count(r => r.StartsWith("PUT")));
        using var back = new MemoryStream();
        await store.DownloadAsync(file.Id, back, default);
        Assert.Equal(content, back.ToArray());
        for (var i = 0; i < 3; i++) await store.UploadAsync($"m{i}.manifest.enc", new MemoryStream([1, 2, 3]), 3, new Dictionary<string, string>(), default);
        Assert.Equal(4, (await store.ListAsync("enc", default)).Count); // paginated two at a time
        await store.DeleteAsync(file.Id, default);
        await store.DeleteAsync(file.Id, default); // idempotent
        Assert.Null(await store.GetAsync(file.Id, default));
        Assert.Equal(BackupErrors.DriveMissing, (await Assert.ThrowsAsync<BackupException>(() => store.DownloadAsync(file.Id, new MemoryStream(), default))).Code);
    }

    [Fact]
    public async Task Failures_map_to_stable_codes_without_details()
    {
        var (store, google, http) = Create();
        google.QuotaExceeded = true;
        Assert.Equal(BackupErrors.DriveQuota, (await Assert.ThrowsAsync<BackupException>(() => store.UploadAsync("a", new MemoryStream([1]), 1, new Dictionary<string, string>(), default))).Code);
        google.QuotaExceeded = false; google.RevokedAccess = true;
        var revoked = new GoogleDriveStore(http, new("client", "secret", google.RefreshToken), "folder-x");
        Assert.Equal(BackupErrors.DriveAuth, (await Assert.ThrowsAsync<BackupException>(() => revoked.GetAsync("anything", default))).Code);
    }

    [Fact]
    public async Task Authorization_url_uses_pkce_offline_access_and_only_the_drive_file_scope()
    {
        var (verifier, challenge) = GoogleDriveApi.Pkce();
        Assert.Equal(challenge, Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        var url = new Uri(GoogleDriveApi.AuthorizationUrl("client-id", "https://localhost:7269/api/admin/backups/drive/callback", "state", challenge));
        var q = System.Web.HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("accounts.google.com", url.Host);
        Assert.Equal(GoogleDriveApi.Scope, q["scope"]); Assert.Equal("offline", q["access_type"]); Assert.Equal("consent", q["prompt"]);
        Assert.Equal("S256", q["code_challenge_method"]); Assert.Equal(challenge, q["code_challenge"]);
        var google = new FakeGoogleHandler();
        var token = await GoogleDriveApi.ExchangeCodeAsync(new HttpClient(google), "client-id", "secret", "good-code", "x", verifier, default);
        Assert.Equal(google.RefreshToken, token.RefreshToken); Assert.Equal(verifier, google.LastCodeVerifier);
    }
}
