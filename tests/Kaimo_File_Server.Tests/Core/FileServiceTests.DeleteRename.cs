using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Search;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public partial class FileServiceTests
{
    // ═══════════════════ DeleteFileAsync ═══════════════════

    [Fact]
    public async Task DeleteFileAsync_WithAccess_CallsStorageDelete()
    {
        var ctx = CreateContext();
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        AllowAccess(FilePermission.Delete);

        await _sut.DeleteFileAsync("/test.txt", ctx, false);
        _storageMock.Verify(s => s.DeleteAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task DeleteFileAsync_PermanentDelete_CleansMetadataAndVersions()
    {
        var versions = new Mock<IFileVersionService>();
        var sut = new FileService(
            _storageMock.Object, _aclMock.Object, null, _shareId, versions.Object);
        var ctx = CreateContext();
        _storageMock.Setup(s => s.IsDirectoryAsync("folder")).ReturnsAsync(true);
        AllowAccess(FilePermission.Delete);

        await sut.DeleteFileAsync("folder", ctx, isRecycleEnabled: false);

        _aclMock.Verify(a => a.DeleteAclAsync(_shareId, "folder"), Times.Once);
        versions.Verify(v => v.DeletePathAsync(_shareId, "folder"), Times.Once);
    }

    [Fact]
    public async Task DeleteFileAsync_DoesNotWaitForSearchIndex()
    {
        var search = new Mock<ISearchService>();
        var searchCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        search.Setup(s => s.onFileDeleted("/storage/doc.txt"))
            .Returns(searchCompletion.Task);
        _storageMock.Setup(s => s.IsDirectoryAsync("doc.txt")).ReturnsAsync(false);
        _storageMock.Setup(s => s.ToAbsolutePath("doc.txt"))
            .Returns("/storage/doc.txt");
        var sut = new FileService(
            _storageMock.Object, _aclMock.Object, search.Object, _shareId);
        var ctx = CreateContext();
        AllowAccess(FilePermission.Delete);

        await sut.DeleteFileAsync("doc.txt", ctx, isRecycleEnabled: false)
            .WaitAsync(TimeSpan.FromSeconds(1));

        search.Verify(s => s.onFileDeleted("/storage/doc.txt"), Times.Once);
        searchCompletion.SetResult();
    }

    [Fact]
    public async Task DeleteFileAsync_RecycleMove_RenamesMetadataAndVersionsToActualPath()
    {
        var versions = new Mock<IFileVersionService>();
        var sut = new FileService(
            _storageMock.Object, _aclMock.Object, null, _shareId, versions.Object);
        var ctx = CreateContext();
        _storageMock.Setup(s => s.IsDirectoryAsync("doc.txt")).ReturnsAsync(false);
        _storageMock.Setup(s => s.MoveAsync("doc.txt", ".RECYCLE_BIN/doc.txt"))
            .ReturnsAsync(".RECYCLE_BIN/doc_20260719.txt");
        AllowAccess(FilePermission.Delete);

        await sut.DeleteFileAsync("doc.txt", ctx, isRecycleEnabled: true);

        _aclMock.Verify(a => a.RenameAclPathAsync(
            _shareId, "doc.txt", ".RECYCLE_BIN/doc_20260719.txt"), Times.Once);
        versions.Verify(v => v.RenamePathAsync(
            _shareId, "doc.txt", ".RECYCLE_BIN/doc_20260719.txt", null), Times.Once);
    }

    [Fact]
    public async Task DeleteFileAsync_RecycleMove_DoesNotMoveCloudSyncMapping()
    {
        var cloudSyncPaths = new Mock<ICloudSyncPathUpdater>();
        var sut = new FileService(
            _storageMock.Object, _aclMock.Object, null, _shareId,
            cloudSyncPathUpdater: cloudSyncPaths.Object);
        _storageMock.Setup(s => s.IsDirectoryAsync("folder")).ReturnsAsync(true);
        _storageMock.Setup(s => s.MoveAsync("folder", ".RECYCLE_BIN/folder"))
            .ReturnsAsync(".RECYCLE_BIN/folder");
        AllowAccess(FilePermission.Delete);

        await sut.DeleteFileAsync("folder", CreateContext(), isRecycleEnabled: true);

        cloudSyncPaths.Verify(updater => updater.RenamePathAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteFileAsync_RecycleBinEntry_IsDeletedPermanently()
    {
        var sut = new FileService(
            _storageMock.Object, _aclMock.Object, null, _shareId);
        var ctx = CreateContext();
        _storageMock.Setup(s => s.IsDirectoryAsync(
            ".RECYCLE_BIN/doc.txt")).ReturnsAsync(false);
        AllowAccess(FilePermission.Delete);

        await sut.DeleteFileAsync(
            ".RECYCLE_BIN/doc.txt", ctx, isRecycleEnabled: true);

        _storageMock.Verify(
            s => s.DeleteAsync(".RECYCLE_BIN/doc.txt"), Times.Once);
        _storageMock.Verify(
            s => s.MoveAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteFileAsync_SimilarRecyclePrefix_IsRecycled()
    {
        var sut = new FileService(
            _storageMock.Object, _aclMock.Object, null, _shareId);
        var ctx = CreateContext();
        const string source = ".RECYCLE_BIN_backup/doc.txt";
        const string destination =
            ".RECYCLE_BIN/.RECYCLE_BIN_backup/doc.txt";
        _storageMock.Setup(s => s.IsDirectoryAsync(source))
            .ReturnsAsync(false);
        _storageMock.Setup(s => s.MoveAsync(source, destination))
            .ReturnsAsync(destination);
        AllowAccess(FilePermission.Delete);

        await sut.DeleteFileAsync(source, ctx, isRecycleEnabled: true);

        _storageMock.Verify(
            s => s.MoveAsync(source, destination), Times.Once);
        _storageMock.Verify(
            s => s.DeleteAsync(It.IsAny<string>()), Times.Never);
    }

    // ═══════════════════ Per-home recycle bin (recycleRootDepth = 1) ═══════════════════

    private FileService CreateHomeSut()
        => new(_storageMock.Object, _aclMock.Object, null, _shareId, recycleRootDepth: 1);

    [Fact]
    public async Task DeleteFileAsync_InHome_RecyclesIntoTheHomesOwnRecycleBin()
    {
        const string source = "uid/docs/doc.txt";
        const string destination = "uid/.RECYCLE_BIN/docs/doc.txt";
        _storageMock.Setup(s => s.IsDirectoryAsync(source)).ReturnsAsync(false);
        _storageMock.Setup(s => s.MoveAsync(source, destination)).ReturnsAsync(destination);
        AllowAccess(FilePermission.Delete);

        await CreateHomeSut().DeleteFileAsync(source, CreateContext(), isRecycleEnabled: true);

        _storageMock.Verify(s => s.MoveAsync(source, destination), Times.Once);
        _aclMock.Verify(a => a.RenameAclPathAsync(_shareId, source, destination), Times.Once);
    }

    [Fact]
    public async Task DeleteFileAsync_InHomeRecycleBin_IsDeletedPermanently()
    {
        const string entry = "uid/.RECYCLE_BIN/doc.txt";
        _storageMock.Setup(s => s.IsDirectoryAsync(entry)).ReturnsAsync(false);
        AllowAccess(FilePermission.Delete);

        await CreateHomeSut().DeleteFileAsync(entry, CreateContext(), isRecycleEnabled: true);

        _storageMock.Verify(s => s.DeleteAsync(entry), Times.Once);
        _storageMock.Verify(s => s.MoveAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CreateFileAsync_InHomeRecycleBin_IsRejectedAsReserved()
    {
        AllowAccess(FilePermission.CreateWriteData);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            CreateHomeSut().CreateFileAsync("uid/.RECYCLE_BIN/new.txt", CreateContext()));
    }

    [Fact]
    public async Task GetMetadataAsync_InHomeInternalNamespace_IsDenied()
    {
        _storageMock.Setup(s => s.GetMetadataAsync("uid/.kaimo-x"))
            .ReturnsAsync(new FileMetadata { Path = "uid/.kaimo-x", Name = ".kaimo-x" });
        AllowAccess(FilePermission.ListReadData);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            CreateHomeSut().GetMetadataAsync("uid/.kaimo-x", CreateContext()));
    }

    [Fact]
    public async Task NotifyExternalDeleteAsync_CleansMetadataAndVersions()
    {
        var versions = new Mock<IFileVersionService>();
        var sut = new FileService(
            _storageMock.Object, _aclMock.Object, null, _shareId, versions.Object);

        await sut.NotifyExternalDeleteAsync("folder", isDirectory: true);

        _aclMock.Verify(a => a.DeleteAclAsync(_shareId, "folder"), Times.Once);
        versions.Verify(v => v.DeletePathAsync(_shareId, "folder"), Times.Once);
    }

    [Fact]
    public async Task DeleteOnClose_CleansMetadataAndVersions()
    {
        var versions = new Mock<IFileVersionService>();
        var handle = new Mock<IStorageHandle>();
        var deleteOnClose = false;
        handle.SetupGet(h => h.IsDirectory).Returns(false);
        handle.SetupGet(h => h.AbsolutePath).Returns("C:/share/doc.txt");
        handle.SetupGet(h => h.DeleteOnClose).Returns(() => deleteOnClose);
        handle.Setup(h => h.MarkDeleteOnClose()).Callback(() => deleteOnClose = true);
        handle.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _storageMock.Setup(s => s.ExistsAsync("doc.txt")).ReturnsAsync(true);
        _storageMock.Setup(s => s.IsDirectoryAsync("doc.txt")).ReturnsAsync(false);
        _storageMock.Setup(s => s.OpenAsync(
                "doc.txt", OpenMode.Open, AccessIntent.Read, ShareIntent.Delete,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        AllowAccess(FilePermission.ListReadData);
        var sut = new FileService(
            _storageMock.Object, _aclMock.Object, null, _shareId, versions.Object);

        var opened = await sut.OpenAsync(
            "doc.txt", OpenMode.Open, AccessIntent.Read, ShareIntent.Delete, CreateContext());
        opened.Session.MarkDeleteOnClose();
        await opened.Session.DisposeAsync();

        _aclMock.Verify(a => a.DeleteAclAsync(_shareId, "doc.txt"), Times.Once);
        versions.Verify(v => v.DeletePathAsync(_shareId, "doc.txt"), Times.Once);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task DirtyClose_SnapshotsVersionUnlessSkipped(bool skipVersioning, int expectedVersions)
    {
        // Cloud-sync pulls skip the snapshot so pulled data is not stored twice.
        var versions = new Mock<IFileVersionService>();
        var handle = new Mock<IStorageHandle>();
        handle.SetupGet(h => h.IsDirectory).Returns(false);
        handle.SetupGet(h => h.IsDirty).Returns(true);
        handle.SetupGet(h => h.AbsolutePath).Returns("C:/share/doc.txt");
        handle.Setup(h => h.GetReadableSnapshot()).Returns(() => new MemoryStream([1, 2, 3]));
        handle.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _storageMock.Setup(s => s.ExistsAsync("doc.txt")).ReturnsAsync(true);
        _storageMock.Setup(s => s.IsDirectoryAsync("doc.txt")).ReturnsAsync(false);
        _storageMock.Setup(s => s.OpenAsync(
                "doc.txt", OpenMode.CreateOrTruncate, AccessIntent.Write, ShareIntent.None,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        AllowAccess(FilePermission.CreateWriteData);
        var sut = new FileService(
            _storageMock.Object, _aclMock.Object, null, _shareId, versions.Object);

        var opened = await sut.OpenAsync(
            "doc.txt", OpenMode.CreateOrTruncate, AccessIntent.Write, ShareIntent.None, CreateContext());
        if (skipVersioning)
            opened.Session.SkipVersioning();
        await opened.Session.DisposeAsync();

        versions.Verify(v => v.CreateVersionAsync(
                _shareId, "doc.txt", It.IsAny<Stream>(), It.IsAny<string>()),
            Times.Exactly(expectedVersions));
    }

    [Fact]
    public async Task RenameAsync_RenamesMetadataAndCompleteVersionHistory()
    {
        var versions = new Mock<IFileVersionService>();
        var sut = new FileService(
            _storageMock.Object, _aclMock.Object, null, _shareId, versions.Object);
        var ctx = CreateContext();
        _storageMock.Setup(s => s.IsDirectoryAsync("old")).ReturnsAsync(true);
        AllowAccess(FilePermission.Delete);
        AllowAccess(FilePermission.CreateWriteData);

        await sut.RenameAsync("old", "new", ctx);

        _aclMock.Verify(a => a.RenameAclPathAsync(_shareId, "old", "new"), Times.Once);
        versions.Verify(v => v.RenamePathAsync(
            _shareId, "old", "new", null), Times.Once);
    }

    [Fact]
    public async Task RenameAsync_Directory_UpdatesCloudSyncLocalPath()
    {
        var cloudSyncPaths = new Mock<ICloudSyncPathUpdater>();
        var sut = new FileService(
            _storageMock.Object, _aclMock.Object, null, _shareId,
            cloudSyncPathUpdater: cloudSyncPaths.Object);
        _storageMock.Setup(s => s.IsDirectoryAsync("old")).ReturnsAsync(true);
        AllowAccess(FilePermission.Delete);
        AllowAccess(FilePermission.CreateWriteData);

        await sut.RenameAsync("old", "new", CreateContext());

        cloudSyncPaths.Verify(updater => updater.RenamePathAsync(
            _shareId, "old", "new"), Times.Once);
    }

    [Fact]
    public async Task RenameAsync_OverlappingActiveSync_IsRejectedBeforeStorageMutation()
    {
        var operations = new InMemoryCloudSyncOperationCoordinator(TimeProvider.System);
        var syncLease = await operations.TryBeginSyncAsync(_shareId, "old");
        Assert.NotNull(syncLease);
        await using var activeSync = syncLease;
        var sut = new FileService(
            _storageMock.Object, _aclMock.Object, null, _shareId,
            cloudSyncOperations: operations);

        await Assert.ThrowsAsync<CloudSyncOperationConflictException>(
            () => sut.RenameAsync("old/sub", "new/sub", CreateContext()));

        _storageMock.Verify(s => s.RenameDirectoryAsync(
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _storageMock.Verify(s => s.RenameFileAsync(
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(".RECYCLE_BIN/old.txt")]
    [InlineData(".kaimo-renamed")]
    public async Task RenameAsync_ReservedDestination_IsDeniedBeforeStorage(
        string destination)
    {
        _storageMock.Setup(s => s.IsDirectoryAsync("old.txt"))
            .ReturnsAsync(false);
        AllowAccess(FilePermission.Delete);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.RenameAsync(
                "old.txt", destination, CreateContext()));

        _storageMock.Verify(
            s => s.RenameFileAsync(
                It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
        _storageMock.Verify(
            s => s.RenameDirectoryAsync(
                It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task ExternalDirectoryRename_UsesDirectorySearchLifecycle()
    {
        var search = new Mock<ISearchService>();
        var cloudSyncPaths = new Mock<ICloudSyncPathUpdater>();
        _storageMock.Setup(s => s.ToAbsolutePath("old"))
            .Returns("/storage/old");
        _storageMock.Setup(s => s.ToAbsolutePath("new"))
            .Returns("/storage/new");
        var sut = new FileService(
            _storageMock.Object, _aclMock.Object, search.Object, _shareId,
            cloudSyncPathUpdater: cloudSyncPaths.Object);

        await sut.NotifyExternalRenameAsync(
            "old", "new", isDirectory: true, Guid.NewGuid());

        search.Verify(s => s.onDirectoryRenamed(
            "/storage/old", "/storage/new"), Times.Once);
        search.Verify(s => s.onFileRenamed(
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        cloudSyncPaths.Verify(updater => updater.RenamePathAsync(
            _shareId, "old", "new"), Times.Once);
    }

    [Fact]
    public async Task DeleteFileAsync_WithoutAccess_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        DenyAccess(FilePermission.Delete);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.DeleteFileAsync("/test.txt", ctx, false));
    }
}
