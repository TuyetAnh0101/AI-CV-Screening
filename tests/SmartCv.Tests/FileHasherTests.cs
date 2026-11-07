using System.Security.Cryptography;
using SmartCv.Core.Uploads;

namespace SmartCv.Tests;

/// <summary>A5: FileHasher – SHA-256 viết hoa 64 ký tự hex, tính dần từ Stream.</summary>
public class FileHasherTests
{
    [Theory]
    [InlineData("", "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    [InlineData("abc", "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")]
    public async Task Known_sha256_values(string text, string expected)
    {
        using var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(text));

        var hash = await FileHasher.ComputeSha256Async(stream);

        Assert.Equal(expected, hash);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToUpperInvariant(), hash);
    }

    [Fact]
    public async Task Sample_cv_file_hash_matches_repository_value()
    {
        // test-files/cv-mau-1.pdf và cv-mau-1-ban-sao.pdf giống hệt từng byte (khác tên) -> cùng hash
        var dir = FindRepoTestFiles();
        await using var a = File.OpenRead(Path.Combine(dir, "cv-mau-1.pdf"));
        await using var b = File.OpenRead(Path.Combine(dir, "cv-mau-1-ban-sao.pdf"));

        var hashA = await FileHasher.ComputeSha256Async(a);
        var hashB = await FileHasher.ComputeSha256Async(b);

        Assert.Equal("8775209D03BD1912B0AB100DFBC8A42D81A0022FF7BE5584C0713BFAE61F05A6", hashA);
        Assert.Equal(hashA, hashB);
    }

    [Fact]
    public async Task Different_content_gives_different_hash()
    {
        using var a = new MemoryStream(TestSupport.Pdf("ban-1"));
        using var b = new MemoryStream(TestSupport.Pdf("ban-2"));

        Assert.NotEqual(await FileHasher.ComputeSha256Async(a), await FileHasher.ComputeSha256Async(b));
    }

    [Fact]
    public async Task Hashes_non_seekable_stream_in_chunks()
    {
        // 5 MB qua luồng không tua được: chứng minh tính dần theo luồng, không cần biết trước độ dài
        var data = new byte[5 * 1024 * 1024];
        new Random(42).NextBytes(data);
        await using var stream = new NonSeekableStream(new MemoryStream(data));

        var hash = await FileHasher.ComputeSha256Async(stream);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), hash);
    }

    [Fact]
    public async Task Respects_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FileHasher.ComputeSha256Async(new MemoryStream(new byte[1024]), cts.Token));
    }

    internal static string FindRepoTestFiles()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "test-files");
            if (File.Exists(Path.Combine(candidate, "cv-mau-1.pdf"))) return candidate;
        }
        throw new DirectoryNotFoundException("Không tìm thấy thư mục test-files ở gốc repo.");
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 64 * 1024));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
