namespace SmartCv.Web.Services;

/// <summary>Owns a live HTTP response/stream; disposal never leaves a CV stream open in a circuit.</summary>
public sealed class DownloadedFile(HttpResponseMessage response, Stream content, string contentType, string fileName, CancellationToken token) : IAsyncDisposable
{
    public const long MaxBytes = 64L * 1024 * 1024;
    public string ContentType { get; } = contentType;
    public string FileName { get; } = fileName;
    public Stream Content { get; } = new LimitedStream(content, token);
    public async ValueTask DisposeAsync() { await Content.DisposeAsync(); response.Dispose(); }

    private sealed class LimitedStream(Stream inner, CancellationToken lifetime) : Stream
    {
        private long read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
            var count = await inner.ReadAsync(buffer, linked.Token); read += count;
            if (read > MaxBytes) throw new IOException("File exceeds the secure transfer limit.");
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
