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
        storage.Setup(s => s.ReadAsync(It.IsAny<string>()))
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
