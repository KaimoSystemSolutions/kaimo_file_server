using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Search;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Unit tests for the change-log → search backend mapping in
/// <see cref="SearchIndexingService.IndexEntryAsync"/>. This is the pure dispatch step, tested
/// without the hosting loop: each change type must invoke the matching <see cref="ISearchService"/>
/// hook with the correct absolute path(s), and an entry for an unknown share must be a no-op.
/// </summary>
public sealed class SearchIndexingServiceTests
{
    private static readonly Guid ShareId = Guid.NewGuid();

    private static (Mock<ISearchService> search, Mock<ISearchAdminService> admin,
        IReadOnlyDictionary<Guid, SearchIndexingService.ShareTarget> shares) Setup()
    {
        var storage = new Mock<IStorageEngine>();
        storage.Setup(s => s.ToAbsolutePath(It.IsAny<string>())).Returns<string>(p => "/abs/" + p);
        storage.Setup(s => s.ReadAsync(It.IsAny<string>(), true))
            .Returns(Task.FromResult<Stream>(new MemoryStream()));

        var search = new Mock<ISearchService>();
        search.SetReturnsDefault(Task.CompletedTask);

        var admin = new Mock<ISearchAdminService>();
        admin.SetReturnsDefault(Task.FromResult(true));

        var shares = new Dictionary<Guid, SearchIndexingService.ShareTarget>
        {
            [ShareId] = new(storage.Object, "MyShare"),
        };
        return (search, admin, shares);
    }

    private static FileChangeLogEntry Entry(
        FileChangeType type, string path, string? oldPath = null, bool isDir = false)
        => new() { ShareId = ShareId, ChangeType = type, Path = path, OldPath = oldPath, IsDirectory = isDir };

    [Theory]
    [InlineData(FileChangeType.Created)]
    [InlineData(FileChangeType.Modified)]
    public async Task File_CreatedOrModified_IndexesContentAtAbsolutePath(FileChangeType type)
    {
        var (search, admin, shares) = Setup();

        await SearchIndexingService.IndexEntryAsync(
            Entry(type, "docs/a.txt"), search.Object, admin.Object, shares);

        search.Verify(s => s.onFileCreated(
            "/abs/docs/a.txt", It.IsAny<Task<Stream>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task File_CreatedWhileLockedByWriter_IndexesNameOnly_ModifiedStillThrows()
    {
        var (search, admin, shares) = Setup();
        var storage = Mock.Get(shares[ShareId].Storage);
        storage.Setup(s => s.ReadAsync(It.IsAny<string>(), true))
            .Throws(new IOException("The process cannot access the file because it is being used by another process."));

        await SearchIndexingService.IndexEntryAsync(
            Entry(FileChangeType.Created, "docs/a.txt"), search.Object, admin.Object, shares);

        search.Verify(s => s.onFileCreated(
            "/abs/docs/a.txt",
            It.Is<Task<Stream>>(t => t.Result == Stream.Null),
            It.IsAny<CancellationToken>()), Times.Once);
        await Assert.ThrowsAsync<IOException>(() => SearchIndexingService.IndexEntryAsync(
            Entry(FileChangeType.Modified, "docs/a.txt"), search.Object, admin.Object, shares));
    }

    [Fact]
    public async Task Directory_Created_IndexesDirectory()
    {
        var (search, admin, shares) = Setup();

        await SearchIndexingService.IndexEntryAsync(
            Entry(FileChangeType.Created, "docs", isDir: true), search.Object, admin.Object, shares);

        search.Verify(s => s.onDirectoryCreated("/abs/docs"), Times.Once);
        search.Verify(s => s.onFileCreated(
            It.IsAny<string>(), It.IsAny<Task<Stream>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task File_Deleted_RemovesFromIndex()
    {
        var (search, admin, shares) = Setup();

        await SearchIndexingService.IndexEntryAsync(
            Entry(FileChangeType.Deleted, "docs/a.txt"), search.Object, admin.Object, shares);

        search.Verify(s => s.onFileDeleted("/abs/docs/a.txt"), Times.Once);
    }

    [Fact]
    public async Task File_Renamed_MovesOldToNewAbsolutePath()
    {
        var (search, admin, shares) = Setup();

        await SearchIndexingService.IndexEntryAsync(
            Entry(FileChangeType.Renamed, "b/a.txt", oldPath: "a/a.txt"),
            search.Object, admin.Object, shares);

        search.Verify(s => s.onFileRenamed("/abs/a/a.txt", "/abs/b/a.txt"), Times.Once);
    }

    [Fact]
    public async Task MovedToRecycleBin_FollowsTheFileThere()
    {
        // Recycle-bin contents are searchable (opt-in at query time), so a delete to the bin
        // is a plain rename in the index.
        var (search, admin, shares) = Setup();

        await SearchIndexingService.IndexEntryAsync(
            Entry(FileChangeType.Renamed, ".RECYCLE_BIN/docs/a.txt", oldPath: "docs/a.txt"),
            search.Object, admin.Object, shares);

        search.Verify(s => s.onFileRenamed("/abs/docs/a.txt", "/abs/.RECYCLE_BIN/docs/a.txt"), Times.Once);
        search.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MovedIntoHiddenFolder_RemovesOldEntry_InsteadOfMovingIt(bool isDir)
    {
        var (search, admin, shares) = Setup();

        await SearchIndexingService.IndexEntryAsync(
            Entry(FileChangeType.Renamed, "docs/.git/a", oldPath: "docs/a", isDir: isDir),
            search.Object, admin.Object, shares);

        if (isDir)
            search.Verify(s => s.onDirectoryDeleted("/abs/docs/a"), Times.Once);
        else
            search.Verify(s => s.onFileDeleted("/abs/docs/a"), Times.Once);
        search.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FileMovedOutOfHiddenFolder_IsIndexedFresh()
    {
        var (search, admin, shares) = Setup();

        await SearchIndexingService.IndexEntryAsync(
            Entry(FileChangeType.Renamed, "docs/a.txt", oldPath: ".tmp/a.txt"),
            search.Object, admin.Object, shares);

        search.Verify(s => s.onFileCreated(
            "/abs/docs/a.txt", It.IsAny<Task<Stream>>(), It.IsAny<CancellationToken>()), Times.Once);
        search.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DirectoryMovedOutOfHiddenFolder_ReindexesShare()
    {
        var (search, admin, shares) = Setup();

        await SearchIndexingService.IndexEntryAsync(
            Entry(FileChangeType.Renamed, "docs", oldPath: ".tmp/docs", isDir: true),
            search.Object, admin.Object, shares);

        admin.Verify(a => a.TryStartReindexAsync("MyShare", It.IsAny<CancellationToken>()), Times.Once);
        search.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(FileChangeType.Created, ".git/a.txt", null)]
    [InlineData(FileChangeType.Modified, "docs/.git/config", null)]
    [InlineData(FileChangeType.Created, ".RECYCLE_BIN/.env", null)]
    [InlineData(FileChangeType.Renamed, ".tmp/b.txt", ".tmp/a.txt")]
    public async Task HiddenPaths_AreNotIndexed(FileChangeType type, string path, string? oldPath)
    {
        var (search, admin, shares) = Setup();

        await SearchIndexingService.IndexEntryAsync(
            Entry(type, path, oldPath), search.Object, admin.Object, shares);

        search.VerifyNoOtherCalls();
        admin.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HomeRecycleBin_IsIndexed()
    {
        var (search, admin, shares) = Setup();
        shares = new Dictionary<Guid, SearchIndexingService.ShareTarget>
        {
            [ShareId] = shares[ShareId] with { RecycleRootDepth = 1 },
        };

        await SearchIndexingService.IndexEntryAsync(
            Entry(FileChangeType.Created, "uid/.RECYCLE_BIN/a.txt"), search.Object, admin.Object, shares);

        search.Verify(s => s.onFileCreated(
            "/abs/uid/.RECYCLE_BIN/a.txt", It.IsAny<Task<Stream>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubtreeChanged_TriggersShareReindex()
    {
        var (search, admin, shares) = Setup();

        await SearchIndexingService.IndexEntryAsync(
            Entry(FileChangeType.SubtreeChanged, "docs", isDir: true),
            search.Object, admin.Object, shares);

        admin.Verify(a => a.TryStartReindexAsync("MyShare", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnknownShare_IsNoOp()
    {
        var (search, admin, shares) = Setup();
        var foreign = new FileChangeLogEntry
        {
            ShareId = Guid.NewGuid(), // not in the share map
            ChangeType = FileChangeType.Created,
            Path = "docs/a.txt",
        };

        await SearchIndexingService.IndexEntryAsync(foreign, search.Object, admin.Object, shares);

        search.VerifyNoOtherCalls();
        admin.VerifyNoOtherCalls();
    }
}
