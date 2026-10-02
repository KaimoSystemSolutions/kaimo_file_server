using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.Sync;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// <see cref="SyncQueryService"/> is the full-tree enumeration behind the client sync API.
/// It must only reveal what the ACL-filtering <see cref="IFileService.ListAsync"/> returns,
/// skip unlistable folders instead of failing, and capture the change cursor BEFORE the walk.
/// </summary>
public sealed class SyncQueryServiceTests
{
    private readonly ShareDefinition _share = new("docs", "/data/docs", null, true, false, false);
    private readonly Mock<IShareRepository> _shares = new();
    private readonly Mock<IFileService> _fs = new();
    private readonly Mock<IFileChangeCursorRepository> _cursors = new();
    private readonly Mock<IFileChangeLogRepository> _changeLog = new();
    private readonly UserContext _user = new(new User(Guid.NewGuid(), "U", "u", "h", "n"), [], [], []);
    private readonly List<string> _calls = [];
    private readonly SyncQueryService _sut;

    public SyncQueryServiceTests()
    {
        _shares.Setup(s => s.GetByIdAsync(_share.Id)).ReturnsAsync(_share);
        var factory = new Mock<IFileServiceFactory>();
        factory.Setup(f => f.CreateForShare(_share.Id, _share.Path)).Returns(_fs.Object);

        var modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _cursors.Setup(c => c.GetShareChangeStateAsync(_share.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("cursor"))
            .ReturnsAsync(new ShareChangeState(modified, 7));
        _changeLog.Setup(c => c.GetHeadSeqAsync(_share.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("seq"))
            .ReturnsAsync(42);

        _sut = new SyncQueryService(_shares.Object, factory.Object, _cursors.Object, _changeLog.Object);
    }

    private void Children(string dir, params FileMetadata[] items)
        => _fs.Setup(f => f.ListAsync(dir, _user))
            .Callback(() => _calls.Add("list:" + dir))
            .ReturnsAsync(items.ToList());

    private static FileMetadata Dir(string path) => new() { Path = path, Name = path.Split('/')[^1], IsDirectory = true };
    private static FileMetadata File(string path, long size = 1) => new() { Path = path, Name = path.Split('/')[^1], Size = size };

    [Fact]
    public async Task UnknownShare_Throws()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _sut.EnumerateAsync(Guid.NewGuid(), "", _user));
    }

    [Fact]
    public async Task WalksTreeDepthFirst_AndReturnsCursorAndSeq()
    {
        Children("", Dir("a"), File("root.txt"));
        Children("a", File("a/x.txt", 5));

        var delta = await _sut.EnumerateAsync(_share.Id, "/", _user);

        Assert.Equal(["a", "a/x.txt", "root.txt"], delta.Entries.Select(e => e.Path));
        Assert.Equal(new ShareChangeState(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 7).ToToken(), delta.Token);
        Assert.Equal(42, delta.Seq);
    }

    [Fact]
    public async Task UnlistableFolder_IsSkipped_NotFatal()
    {
        Children("", Dir("secret"), Dir("open"));
        _fs.Setup(f => f.ListAsync("secret", _user)).ThrowsAsync(new UnauthorizedAccessException());
        Children("open", File("open/a.txt"));

        var delta = await _sut.EnumerateAsync(_share.Id, "", _user);

        // The folder entry itself was visible in its parent listing; its content is not.
        Assert.Equal(["secret", "open", "open/a.txt"], delta.Entries.Select(e => e.Path));
    }

    [Fact]
    public async Task CursorAndSeq_AreCapturedBeforeTheWalk()
    {
        Children("sub", File("sub/a.txt"));

        await _sut.EnumerateAsync(_share.Id, "sub", _user);

        Assert.Equal(["cursor", "seq", "list:sub"], _calls);
        _cursors.Verify(c => c.GetShareChangeStateAsync(_share.Id, "sub", It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Cancellation_StopsTheWalk()
    {
        Children("", File("a.txt"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sut.EnumerateAsync(_share.Id, "", _user, cts.Token));
    }
}
