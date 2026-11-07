using SmartCv.Infrastructure.Storage;

namespace SmartCv.Tests;

/// <summary>A6: LocalCvFileStorage – tên GUID, thư mục yyyy/MM, không ghi đè, chặn đường dẫn ra ngoài, dọn file ghi dở.</summary>
public class LocalCvFileStorageTests
{
    private static readonly DateTime Nov2026 = new(2026, 11, 7, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Saves_under_year_month_with_guid_name_and_forward_slashes()
    {
        var root = TestSupport.TempRoot();
        var storage = TestSupport.Storage(root);
        var id = Guid.NewGuid();

        var stored = await storage.SaveAsync(id, ".PDF", new MemoryStream([1, 2, 3]), Nov2026);

        Assert.Equal($"Uploads/CV/2026/11/{id}.pdf", stored);
        Assert.True(File.Exists(Path.Combine(root, "Uploads", "CV", "2026", "11", $"{id}.pdf")));
        Assert.True(storage.Exists(stored));
    }

    [Fact]
    public async Task Never_overwrites_existing_file()
    {
        var storage = TestSupport.Storage(TestSupport.TempRoot());
        var id = Guid.NewGuid();
        var stored = await storage.SaveAsync(id, ".pdf", new MemoryStream([1]), Nov2026);

        await Assert.ThrowsAsync<IOException>(() => storage.SaveAsync(id, ".pdf", new MemoryStream([9, 9]), Nov2026));

        await using var s = storage.OpenRead(stored);
        Assert.Equal(1, s.Length);   // nội dung cũ còn nguyên
    }

    [Theory]
    [InlineData("../../../Windows/win.ini")]
    [InlineData("Uploads/CV/../../appsettings.json")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("Uploads/CV-khac/x.pdf")]
    public void Blocks_paths_outside_storage_root(string storedPath)
    {
        var storage = TestSupport.Storage(TestSupport.TempRoot());

        Assert.Throws<InvalidOperationException>(() => storage.Exists(storedPath));
        Assert.Throws<InvalidOperationException>(() => storage.OpenRead(storedPath));
        Assert.Throws<InvalidOperationException>(() => storage.Delete(storedPath));
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("./../x")]
    [InlineData(@".pdf\..\x")]
    public async Task Rejects_invalid_extension(string extension)
    {
        var storage = TestSupport.Storage(TestSupport.TempRoot());

        await Assert.ThrowsAsync<ArgumentException>(() => storage.SaveAsync(Guid.NewGuid(), extension, new MemoryStream([1]), Nov2026));
    }

    [Fact]
    public async Task Partial_write_is_deleted_when_copy_fails()
    {
        var root = TestSupport.TempRoot();
        var storage = TestSupport.Storage(root);

        await Assert.ThrowsAsync<IOException>(() =>
            storage.SaveAsync(Guid.NewGuid(), ".pdf", new BrokenStream(bytesBeforeFailure: 200_000), Nov2026));

        Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Partial_write_is_deleted_when_cancelled()
    {
        var root = TestSupport.TempRoot();
        var storage = TestSupport.Storage(root);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            storage.SaveAsync(Guid.NewGuid(), ".pdf", new MemoryStream(new byte[100_000]), Nov2026, cts.Token));

        Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Delete_missing_file_is_noop()
    {
        var storage = TestSupport.Storage(TestSupport.TempRoot());
        storage.Delete($"Uploads/CV/2026/11/{Guid.NewGuid()}.pdf");
    }

    /// <summary>Luồng đọc được một phần rồi báo lỗi (giả lập client ngắt kết nối giữa chừng).</summary>
    private sealed class BrokenStream(int bytesBeforeFailure) : Stream
    {
        private int _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= bytesBeforeFailure) throw new IOException("Kết nối bị ngắt (giả lập)");
            var n = Math.Min(count, bytesBeforeFailure - _read);
            _read += n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
