using System.Net;
using System.Text;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed partial class WebDavStorageProviderTests
{
    // ── Timeouts ──

    [Fact]
    public async Task Upload_LongerThanIdleTimeout_SucceedsWhileDataKeepsFlowing()
    {
        var store = TimedStore(new DrainingHandler(), TimeSpan.FromSeconds(2));

        // 50 chunks x 50 ms = 2.5 s total, beyond the 2 s idle timeout. What matters for
        // flakiness is the gap between one read and the next vs. the timeout: ~1.95 s of
        // slack absorbs thread-pool starvation on busy CI runners (500 ms was not enough).
        await store.WriteAsync("/big.bin", new SlowStream(chunks: 50, delay: TimeSpan.FromMilliseconds(50)), overwrite: true);
    }

    [Fact]
    public async Task Upload_StalledSource_TimesOut()
    {
        var store = TimedStore(new DrainingHandler(), TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<TimeoutException>(() => store.WriteAsync(
            "/big.bin", new SlowStream(chunks: 2, delay: TimeSpan.FromSeconds(5)), overwrite: true));
    }

    [Fact]
    public async Task MetadataRequest_UnresponsiveServer_TimesOut()
    {
        var store = TimedStore(new DrainingHandler(responseDelay: TimeSpan.FromSeconds(5)), TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<TimeoutException>(() => store.CreateDirectoryAsync("/folder"));
    }

    [Fact]
    public async Task CallerCancellation_IsNotReportedAsTimeout()
    {
        var store = TimedStore(new DrainingHandler(responseDelay: TimeSpan.FromSeconds(5)), TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.CreateDirectoryAsync("/folder", cts.Token));
        Assert.IsNotType<TimeoutException>(exception);
    }

    private static WebDavRemoteFileStore TimedStore(HttpMessageHandler handler, TimeSpan idleTimeout)
        => new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, "https://nas.example/dav", idleTimeout);

    /// <summary>Consumes the request body like a server would, then answers 201.</summary>
    private sealed class DrainingHandler(TimeSpan? responseDelay = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                await request.Content.CopyToAsync(Stream.Null, cancellationToken);
            if (responseDelay is { } delay)
                await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    /// <summary>Yields one 1 KiB chunk per <c>delay</c>; not seekable, like a network source.</summary>
    private sealed class SlowStream(int chunks, TimeSpan delay) : Stream
    {
        private int _remaining = chunks;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining-- <= 0)
                return 0;
            await Task.Delay(delay, cancellationToken);
            int size = Math.Min(1024, buffer.Length);
            buffer.Span[..size].Fill(0x42);
            return size;
        }
    }
}
