namespace Kaimo_File_Server.Web.DynamicHelpers;

public class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly long _totalBytes;
    private readonly Action<long, long> _onProgress;
    private long _bytesRead;

    public ProgressStream(Stream inner, long totalBytes, Action<long, long> onProgress)
    {
        _inner = inner;
        _totalBytes = totalBytes;
        _onProgress = onProgress;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
        _bytesRead += read;
        _onProgress(_bytesRead, _totalBytes);
        return read;
    }

    // Delegate everything else
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _totalBytes;
    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
        => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();
    public override void SetLength(long value)
        => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();
}