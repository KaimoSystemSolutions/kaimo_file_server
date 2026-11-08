using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Web.Controllers.WebDav;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Services;

/// <summary>Remote content has no versions or recycle bin, so a failed or stale write must not cost data.</summary>
public sealed class RemoteMountFileServiceSafetyTests : IDisposable
{
    private readonly MountTestHarness _h = new();
    private DateTime Stored => _h.Remote.ModifiedAt;

    public RemoteMountFileServiceSafetyTests() => _h.Remote.Files["root/a.txt"] = "hello"u8.ToArray();

    public void Dispose() => _h.Dispose();

    private string Text(string remote) => Encoding.UTF8.GetString(_h.Remote.Files[remote]);

    /// <summary>A request body: delivers a few bytes, then optionally fails like an aborted client.</summary>
    private sealed class RequestBody(string content, bool failAtTheEnd) : Stream
    {
        private readonly MemoryStream _inner = new(Encoding.UTF8.GetBytes(content));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            if (read == 0 && failAtTheEnd) throw new IOException("The client aborted the upload.");
            return read;
        }
    }

    // ── aborted uploads ──────────────────────────────────────────────

    [Fact]
    public async Task AbortedUpload_LeavesTheExistingFileUntouched()
    {
        await Assert.ThrowsAsync<IOException>(() =>
            _h.NewAdapter().WriteFileAsync("Team/Cloud/a.txt", new RequestBody("par", failAtTheEnd: true), _h.User));
        Assert.Equal("hello", Text("root/a.txt"));
    }

    [Fact]
    public async Task AbortedUpload_OfANewFile_LeavesNothingBehind()
    {
        await Assert.ThrowsAsync<IOException>(() =>
            _h.NewAdapter().WriteFileAsync("Team/Cloud/new.txt", new RequestBody("par", failAtTheEnd: true), _h.User));
        Assert.False(_h.Remote.Files.ContainsKey("root/new.txt"));
    }

    [Fact]
    public async Task CompleteRequestBody_ArrivesInFull()
    {
        await _h.NewAdapter().WriteFileAsync("Team/Cloud/a.txt", new RequestBody("complete", failAtTheEnd: false), _h.User);
        Assert.Equal("complete", Text("root/a.txt"));
    }

    // ── time contract of an overwrite ────────────────────────────────

    private Task WriteAsync(string path, DateTime modified, DateTime? created = null)
        => _h.NewAdapter().WriteFileAsync(path, new MemoryStream("x"u8.ToArray()), _h.User, modified, created);

    [Fact]
    public async Task Overwrite_WithAnOlderModifiedTime_KeepsTheWriteTime()
    {
        await WriteAsync("Team/Cloud/a.txt", Stored.AddDays(-1));
        Assert.Empty(_h.Remote.Stamped);
    }

    [Fact]
    public async Task Overwrite_WithANewerModifiedTime_IsStamped()
    {
        await WriteAsync("Team/Cloud/a.txt", Stored.AddDays(1));
        Assert.Equal(Stored.AddDays(1), Assert.Single(_h.Remote.Stamped).Modified);
    }

    [Fact]
    public async Task Overwrite_AdoptsAnOlderCreationTime()
    {
        await WriteAsync("Team/Cloud/a.txt", Stored.AddDays(1), Stored.AddDays(-5));
        Assert.Equal(Stored.AddDays(-5), Assert.Single(_h.Remote.Stamped).Created);
    }

    [Fact]
    public async Task Overwrite_IgnoresANewerCreationTime()
    {
        await WriteAsync("Team/Cloud/a.txt", Stored.AddDays(1), Stored.AddDays(1));
        Assert.Null(Assert.Single(_h.Remote.Stamped).Created);
    }

    [Fact]
    public async Task NewFile_TakesBothSourceTimes()
    {
        await WriteAsync("Team/Cloud/new.txt", Stored.AddDays(-1), Stored.AddDays(-2));
        var stamp = Assert.Single(_h.Remote.Stamped);
        Assert.Equal(Stored.AddDays(-1), stamp.Modified);
        Assert.Equal(Stored.AddDays(-2), stamp.Created);
    }

    // ── unknown times are not 1970 ───────────────────────────────────

    [Fact]
    public async Task CopyOfTheMountRoot_DoesNotDateTheTargetTo1970()
    {
        await WebDavCopy.CopyAsync(_h.NewAdapter(), "Team/Cloud", _h.Factory.Object.CreateForShare(_h.Host), "Team/Copy",
            _h.User, infinity: true, CancellationToken.None, preserveTimes: true);

        Assert.True(Directory.GetLastWriteTimeUtc(Path.Combine(_h.HostPath, "Team", "Copy")).Year > 1970);
    }

    [Fact]
    public async Task CopyOfAFileWithoutATime_DoesNotDateItTo1970()
    {
        _h.Remote.ModifiedAt = DateTime.UnixEpoch;

        await WebDavCopy.CopyAsync(_h.NewAdapter(), "Team/Cloud/a.txt", _h.Factory.Object.CreateForShare(_h.Host), "Team/b.txt",
            _h.User, infinity: true, CancellationToken.None, preserveTimes: true);

        Assert.True(File.GetLastWriteTimeUtc(Path.Combine(_h.HostPath, "Team", "b.txt")).Year > 1970);
    }

    // ── listings behind a write ──────────────────────────────────────

    [Fact]
    public async Task WithoutFreshListings_AnExternalChangeStaysUnseenUntilTheCacheExpires()
    {
        await _h.NewAdapter().ListAsync("Team/Cloud", _h.User);
        _h.Remote.Files["root/external.txt"] = [];

        Assert.Null(await _h.NewAdapter().FindEntryAsync("Team/Cloud/external.txt", _h.User));
    }

    [Fact]
    public async Task WithFreshListings_AnExternalChangeIsSeen()
    {
        await _h.NewAdapter().ListAsync("Team/Cloud", _h.User);
        _h.Remote.Files["root/external.txt"] = [];

        Assert.NotNull(await _h.NewAdapter(freshListings: true).FindEntryAsync("Team/Cloud/external.txt", _h.User));
    }

    [Fact]
    public async Task WithFreshListings_AFolderIsReloadedOncePerRequest()
    {
        await _h.NewAdapter().ListAsync("Team/Cloud", _h.User);
        var adapter = _h.NewAdapter(freshListings: true);

        await adapter.FindEntryAsync("Team/Cloud/a.txt", _h.User);
        await adapter.FindEntryAsync("Team/Cloud/a.txt", _h.User);

        Assert.Equal(2, _h.Remote.ListCalls["root"]);
    }
}
