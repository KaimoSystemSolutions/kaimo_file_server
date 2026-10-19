using Kaimo_File_Server.Core.Services.File;

namespace Kaimo_File_Server.Core.Services.DataServices;

/// <summary>
/// Forward-only write stream that funnels a download straight into an
/// <see cref="IFileSession"/> at increasing offsets, so a pulled file is written
/// to local storage once with no intermediate buffer. It never disposes the
/// session: the caller owns the session's lifetime (and its close-time hooks).
/// </summary>
internal sealed class FileSessionWriteStream(IFileSession session) : Stream
{
    private long _position;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _position;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return;
        await session.WriteAsync(_position, buffer, cancellationToken);
        _position += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken cancellationToken)
        => session.FlushAsync(cancellationToken).AsTask();

    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
