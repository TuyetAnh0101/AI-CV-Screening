using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SmartCv.Core.Backup;

namespace SmartCv.Infrastructure.Backup;

/// <summary>The 256-bit backup key. Its id is published; the key itself never leaves the worker machine except via operator escrow.</summary>
public sealed class BackupKey : IDisposable
{
    public const int Length = 32;
    private readonly byte[] material;
    private readonly byte[] nameKey;

    public BackupKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != Length) throw new BackupException(BackupErrors.Key);
        material = key.ToArray();
        KeyIdBytes = SHA256.HashData([.. Encoding.ASCII.GetBytes("SCApp.Backup.KeyId.v1"), .. material])[..16];
        KeyId = Convert.ToHexStringLower(KeyIdBytes);
        nameKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, material, 32, info: Encoding.ASCII.GetBytes("SCApp.Backup.ObjectName.v1"));
    }

    public byte[] KeyIdBytes { get; }
    public string KeyId { get; }

    /// <summary>Drive object name for a CV: does not reveal the content hash to anyone without the key.</summary>
    public string ObjectKey(string plainSha256) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(nameKey, Encoding.ASCII.GetBytes("cv:" + plainSha256.ToLowerInvariant())));

    internal byte[] FileKey(ReadOnlySpan<byte> salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, material, 32, salt.ToArray(), Encoding.ASCII.GetBytes("SCApp.Backup.File.v1"));

    public static string Format(ReadOnlySpan<byte> key) => Convert.ToBase64String(key);
    public static byte[] Parse(string text)
    {
        try { var bytes = Convert.FromBase64String(text.Trim()); if (bytes.Length == Length) return bytes; }
        catch (FormatException) { }
        throw new BackupException(BackupErrors.Key);
    }

    public void Dispose() { CryptographicOperations.ZeroMemory(material); CryptographicOperations.ZeroMemory(nameKey); }
}

public sealed record BackupCipherResult(long PlainBytes, string PlainSha256, long CipherBytes, string CipherSha256, string CipherMd5);

/// <summary>
/// Streaming AES-256-GCM. A random salt derives a per-file key (HKDF), so chunk counters never repeat a nonce under
/// one key. Every chunk authenticates the header, its index and a final flag: reordering, truncation and appended
/// data are all rejected.
/// Layout: "SCAPPBK1" | version | keyId(16) | salt(16) | chunkSize(4) | { length(4) | ciphertext | tag(16) }*
/// </summary>
public static class BackupCrypto
{
    private static readonly byte[] Magic = "SCAPPBK1"u8.ToArray();
    private const byte Version = 1;
    public const int HeaderLength = 8 + 1 + 16 + 16 + 4;
    public const int ChunkSize = 1024 * 1024;
    private const int TagLength = 16;

    public static async Task<BackupCipherResult> EncryptAsync(BackupKey key, Stream input, Stream output, CancellationToken ct)
    {
        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0); header[8] = Version; key.KeyIdBytes.CopyTo(header, 9);
        RandomNumberGenerator.Fill(header.AsSpan(25, 16));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(41), ChunkSize);
        var fileKey = key.FileKey(header.AsSpan(25, 16));
        using var aes = new AesGcm(fileKey, TagLength);
        using var plainHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var cipherHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var cipherMd5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        long plainTotal = 0, cipherTotal = 0;
        async Task Write(byte[] buffer, int count)
        {
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
            cipherHash.AppendData(buffer, 0, count); cipherMd5.AppendData(buffer, 0, count); cipherTotal += count;
        }
        await Write(header, header.Length);
        var current = new byte[ChunkSize]; var next = new byte[ChunkSize];
        var currentLength = await Fill(input, current, ct);
        var cipher = new byte[ChunkSize]; var tag = new byte[TagLength]; var prefix = new byte[4];
        for (long index = 0; ; index++)
        {
            var nextLength = currentLength == ChunkSize ? await Fill(input, next, ct) : 0;
            var final = nextLength == 0;
            plainHash.AppendData(current, 0, currentLength); plainTotal += currentLength;
            aes.Encrypt(Nonce(index), current.AsSpan(0, currentLength), cipher.AsSpan(0, currentLength), tag, Aad(header, index, final));
            BinaryPrimitives.WriteInt32LittleEndian(prefix, currentLength);
            await Write(prefix, 4); await Write(cipher, currentLength); await Write(tag, TagLength);
            if (final) break;
            (current, next) = (next, current); currentLength = nextLength;
        }
        CryptographicOperations.ZeroMemory(fileKey);
        return new(plainTotal, Hex(plainHash), cipherTotal, Hex(cipherHash), Hex(cipherMd5));
    }

    /// <summary>
    /// Which key encrypted this stream, read from its header (unauthenticated: decryption still verifies it).
    /// Leaves a seekable stream at its start.
    /// </summary>
    public static async Task<string> ReadKeyIdAsync(Stream input, CancellationToken ct)
    {
        var header = new byte[HeaderLength];
        input.Position = 0;
        if (await Fill(input, header, ct) != HeaderLength || !header.AsSpan(0, 8).SequenceEqual(Magic)) throw new BackupException(BackupErrors.Checksum);
        input.Position = 0;
        return Convert.ToHexStringLower(header.AsSpan(9, 16));
    }

    /// <summary>Writes plaintext only after its chunk authenticated. Throws <see cref="BackupException"/> on any mismatch.</summary>
    public static async Task<BackupCipherResult> DecryptAsync(BackupKey key, Stream input, Stream output, CancellationToken ct)
    {
        var header = new byte[HeaderLength];
        if (await Fill(input, header, ct) != HeaderLength || !header.AsSpan(0, 8).SequenceEqual(Magic) || header[8] != Version)
            throw new BackupException(BackupErrors.Checksum);
        if (!header.AsSpan(9, 16).SequenceEqual(key.KeyIdBytes)) throw new BackupException(BackupErrors.KeyMismatch);
        var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(41));
        if (chunkSize is <= 0 or > 16 * 1024 * 1024) throw new BackupException(BackupErrors.Checksum);
        var fileKey = key.FileKey(header.AsSpan(25, 16));
        using var aes = new AesGcm(fileKey, TagLength);
        using var plainHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var cipherHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var cipherMd5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        cipherHash.AppendData(header); cipherMd5.AppendData(header);
        long plainTotal = 0, cipherTotal = HeaderLength;
        var prefix = new byte[4]; var cipher = new byte[chunkSize]; var plain = new byte[chunkSize]; var tag = new byte[TagLength];
        // Every chunk is full-size except the last one, so a short chunk must be the final one.
        for (long index = 0; ; index++)
        {
            if (await Fill(input, prefix, ct) != 4) throw new BackupException(BackupErrors.Checksum);
            var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            if (length < 0 || length > chunkSize || await Fill(input, cipher.AsMemory(0, length), ct) != length || await Fill(input, tag, ct) != TagLength)
                throw new BackupException(BackupErrors.Checksum);
            var final = length < chunkSize || !await HasMore(input, ct);
            try { aes.Decrypt(Nonce(index), cipher.AsSpan(0, length), tag, plain.AsSpan(0, length), Aad(header, index, final)); }
            catch (CryptographicException) { throw new BackupException(BackupErrors.Checksum); }
            cipherHash.AppendData(prefix); cipherHash.AppendData(cipher, 0, length); cipherHash.AppendData(tag);
            cipherMd5.AppendData(prefix); cipherMd5.AppendData(cipher, 0, length); cipherMd5.AppendData(tag);
            cipherTotal += 4 + length + TagLength;
            plainHash.AppendData(plain, 0, length); plainTotal += length;
            await output.WriteAsync(plain.AsMemory(0, length), ct);
            if (final) break;
        }
        if (await HasMore(input, ct)) throw new BackupException(BackupErrors.Checksum);
        CryptographicOperations.ZeroMemory(fileKey); CryptographicOperations.ZeroMemory(plain);
        return new(plainTotal, Hex(plainHash), cipherTotal, Hex(cipherHash), Hex(cipherMd5));
    }

    private static byte[] Nonce(long index) { var nonce = new byte[12]; BinaryPrimitives.WriteInt64LittleEndian(nonce.AsSpan(4), index); return nonce; }
    private static byte[] Aad(byte[] header, long index, bool final)
    {
        var aad = new byte[HeaderLength + 9];
        header.CopyTo(aad, 0); BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(HeaderLength), index); aad[^1] = final ? (byte)1 : (byte)0;
        return aad;
    }
    private static string Hex(IncrementalHash hash) => Convert.ToHexStringLower(hash.GetHashAndReset());

    private static async Task<int> Fill(Stream input, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length) { var read = await input.ReadAsync(buffer[total..], ct); if (read == 0) break; total += read; }
        return total;
    }
    private static Task<int> Fill(Stream input, byte[] buffer, CancellationToken ct) => Fill(input, buffer.AsMemory(), ct);

    // Peeks one byte through a pushback-capable wrapper.
    private static async Task<bool> HasMore(Stream input, CancellationToken ct)
    {
        if (input is PeekStream peek) return await peek.HasMoreAsync(ct);
        throw new InvalidOperationException("Decrypt input must be wrapped with BackupCrypto.Readable.");
    }

    /// <summary>Wrap any input passed to <see cref="DecryptAsync"/>.</summary>
    public static Stream Readable(Stream inner) => new PeekStream(inner);

    private sealed class PeekStream(Stream inner) : Stream
    {
        private int pending = -1;
        public async Task<bool> HasMoreAsync(CancellationToken ct)
        {
            if (pending >= 0) return true;
            var one = new byte[1];
            if (await inner.ReadAsync(one, ct) == 0) return false;
            pending = one[0]; return true;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.Length == 0) return 0;
            if (pending >= 0) { buffer.Span[0] = (byte)pending; pending = -1; return 1 + (buffer.Length > 1 ? await inner.ReadAsync(buffer[1..], ct) : 0); }
            return await inner.ReadAsync(buffer, ct);
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
