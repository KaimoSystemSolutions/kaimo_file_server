using System.Diagnostics;

namespace Kaimo_File_Server.Core.Helpers;

/// <summary>
/// A pass-through stream that caps throughput at a fixed number of bytes per
/// second by delaying reads/writes once the transferred volume runs ahead of a
/// wall-clock budget. Used for cloud-sync transfer limits and for public
/// share-link download rate caps.
/// </summary>
public sealed class RateLimitedStream(Stream inner, long bytesPerSecond) : Stream
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _transferred;

    /// <summary>
    /// Wraps <paramref name="stream"/> in a rate limiter when a positive cap is
    /// given; otherwise returns the stream unchanged (null / non-positive = unlimited).
    /// </summary>
    public static Stream Wrap(Stream stream, long? bytesPerSecond)
        => bytesPerSecond is > 0 ? new RateLimitedStream(stream, bytesPerSecond.Value) : stream;

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
