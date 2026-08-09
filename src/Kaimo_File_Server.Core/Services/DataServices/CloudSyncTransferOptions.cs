using System.Diagnostics;
using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Services.DataServices;

/// <summary>Runtime helpers for applying one mapping's persisted transfer constraints.</summary>
public sealed class CloudSyncTransferOptions
{
    private readonly CloudSyncAdvancedSettings _settings;

    public CloudSyncTransferOptions(CloudSyncAdvancedSettings? settings)
        => _settings = settings?.Clone() ?? new CloudSyncAdvancedSettings();

    public bool ShouldSkip(string name, long size)
    {
        if (_settings.MaxFileSizeBytes is > 0 and var maximum && size > maximum)
            return true;

        string extension = Path.GetExtension(name);
        return extension.Length > 0 && (_settings.ExcludedExtensions ?? []).Contains(extension);
    }

    public Stream LimitUpload(Stream stream) => Limit(stream, _settings.MaxUploadBytesPerSecond);
    public Stream LimitDownload(Stream stream) => Limit(stream, _settings.MaxDownloadBytesPerSecond);

    private static Stream Limit(Stream stream, long? bytesPerSecond)
        => bytesPerSecond is > 0 ? new RateLimitedStream(stream, bytesPerSecond.Value) : stream;

    private sealed class RateLimitedStream(Stream inner, long bytesPerSecond) : Stream
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _transferred;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken);
            await ThrottleAsync(read, cancellationToken);
            return read;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            await ThrottleAsync(buffer.Length, cancellationToken);
        }
        private async Task ThrottleAsync(int bytes, CancellationToken cancellationToken)
        {
            if (bytes <= 0) return;
            _transferred += bytes;
            var wait = TimeSpan.FromSeconds((double)_transferred / bytesPerSecond) - _clock.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
    }
}
