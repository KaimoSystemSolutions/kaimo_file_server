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
    // ═══════════════════ ReadFileAsync ═══════════════════

    [Fact]
    public async Task ReadFileAsync_WithAccess_ReturnsStream()
    {
        var ctx = CreateContext();
        var stream = new MemoryStream([1, 2, 3]);
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        AllowAccess(FilePermission.ListReadData);
        _storageMock.Setup(s => s.ReadAsync(It.IsAny<string>())).ReturnsAsync(stream);

        Assert.Same(stream, await _sut.ReadFileAsync("/test.txt", ctx));
    }

    [Fact]
    public async Task ReadFileAsync_WithoutAccess_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        DenyAccess(FilePermission.ListReadData);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.ReadFileAsync("/test.txt", ctx));
    }

    // ═══════════════════ WriteFileAsync ═══════════════════

    [Fact]
    public async Task WriteFileAsync_WithAccess_CallsStorageWrite()
    {
        var ctx = CreateContext();
        var data = new MemoryStream([1, 2, 3]);
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        AllowAccess(FilePermission.CreateWriteData);

        await _sut.WriteFileAsync("/test.txt", data, ctx);
        _storageMock.Verify(s => s.WriteAsync(It.IsAny<string>(), data), Times.Once);
    }

    [Fact]
    public async Task WriteFileAsync_WithoutAccess_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        DenyAccess(FilePermission.CreateWriteData);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.WriteFileAsync("/test.txt", Stream.Null, ctx));
    }

    [Theory]
    [InlineData(".RECYCLE_BIN/upload.txt")]
    [InlineData(".kaimo-upload")]
    public async Task WriteFileAsync_ReservedNamespace_IsDeniedBeforeStorage(
        string path)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.WriteFileAsync(path, Stream.Null, CreateContext()));

        _storageMock.Verify(
            s => s.WriteAsync(
                It.IsAny<string>(), It.IsAny<Stream>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _aclMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WriteFileAsync_WithVersioning_SnapshotsWrittenContent()
    {
        // A web upload/overwrite must build the same version history that SMB
        // writes do, so the version service is invoked with the new content.
        var versionMock = new Mock<IFileVersionService>();
        var sut = new FileService(
            _storageMock.Object, _aclMock.Object, null, _shareId, versionMock.Object);

        var ctx = CreateContext();
        var data = new MemoryStream([1, 2, 3]);
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storageMock.Setup(s => s.ReadAsync(It.IsAny<string>(), true))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3]));
        AllowAccess(FilePermission.CreateWriteData);

        await sut.WriteFileAsync("/test.txt", data, ctx);

        versionMock.Verify(v => v.CreateVersionAsync(
            _shareId, "test.txt", It.IsAny<Stream>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task WriteFileAsync_VersioningFailure_DoesNotFailWrite()
    {
        // Versioning is a best-effort side effect: if it throws, the upload
        // itself must still succeed.
        var versionMock = new Mock<IFileVersionService>();
        versionMock
            .Setup(v => v.CreateVersionAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .ThrowsAsync(new IOException("boom"));
        var sut = new FileService(
            _storageMock.Object, _aclMock.Object, null, _shareId, versionMock.Object);

        var ctx = CreateContext();
        var data = new MemoryStream([1, 2, 3]);
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storageMock.Setup(s => s.ReadAsync(It.IsAny<string>(), true))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3]));
        AllowAccess(FilePermission.CreateWriteData);

        await sut.WriteFileAsync("/test.txt", data, ctx);

        _storageMock.Verify(s => s.WriteAsync(
            It.IsAny<string>(), data, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyExternalClose_UsesCapturedHandleBytes_NotLivePath()
    {
        var versionMock = new Mock<IFileVersionService>();
        var searchMock = new Mock<ISearchService>();
        var capturedBytes = new byte[] { 1, 2, 3, 4 };
        byte[]? versioned = null;
        byte[]? indexed = null;
        var opens = 0;
        versionMock
            .Setup(v => v.CreateVersionAsync(
                _shareId, "test.txt", It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns(async (
                Guid _, string _, Stream content, string? _) =>
            {
                using var copy = new MemoryStream();
                await content.CopyToAsync(copy);
                versioned = copy.ToArray();
                return null;
            });
        searchMock
            .Setup(s => s.onFileCreated(
                "/storage/test.txt", It.IsAny<Task<Stream>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                string _, Task<Stream> contentTask, CancellationToken _) =>
            {
                await using var content = await contentTask;
                using var copy = new MemoryStream();
                await content.CopyToAsync(copy);
                indexed = copy.ToArray();
            });
        _storageMock.Setup(s => s.ToAbsolutePath("test.txt"))
            .Returns("/storage/test.txt");
        var sut = new FileService(
            _storageMock.Object, _aclMock.Object, searchMock.Object, _shareId,
            versionMock.Object);
        var user = CreateContext();

        await sut.NotifyExternalCloseAsync(
            "test.txt", user,
            () =>
            {
                opens++;
                return Task.FromResult<Stream>(
                    new MemoryStream(capturedBytes, writable: false));
            });

        Assert.Equal(capturedBytes, versioned);
        Assert.Equal(capturedBytes, indexed);
        Assert.Equal(2, opens);
        _storageMock.Verify(
            s => s.ReadAsync(It.IsAny<string>(), true), Times.Never);
    }

    [Fact]
    public async Task WriteFileAsync_SearchFailure_DoesNotFailPersistedUpload()
    {
        // Search is a rebuildable side effect. In particular, first-use index
        // initialization may fail transiently and must not make the web layer delete
        // a file that storage already persisted successfully.
        var searchMock = new Mock<ISearchService>();
        searchMock
            .Setup(s => s.onFileCreated(
                It.IsAny<string>(), It.IsAny<Task<Stream>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("search temporarily unavailable"));
        var sut = new FileService(
            _storageMock.Object, _aclMock.Object, searchMock.Object, _shareId);

        var ctx = CreateContext();
        var data = new MemoryStream([1, 2, 3]);
        var indexedContent = new MemoryStream([1, 2, 3]);
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storageMock.Setup(s => s.ReadAsync(It.IsAny<string>(), true)).ReturnsAsync(indexedContent);
        _storageMock.Setup(s => s.ToAbsolutePath("test.txt")).Returns("/storage/test.txt");
        AllowAccess(FilePermission.CreateWriteData);

        await sut.WriteFileAsync("/test.txt", data, ctx);

        _storageMock.Verify(s => s.WriteAsync(
            "test.txt", data, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(indexedContent.CanRead);
    }

    [Fact]
    public async Task WriteFileAsync_DoesNotWaitForSearchIndex()
    {
        var search = new Mock<ISearchService>();
        var searchCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        search.Setup(s => s.onFileCreated(
                "/storage/test.txt",
                It.IsAny<Task<Stream>>(),
                It.IsAny<CancellationToken>()))
            .Returns(searchCompletion.Task);
        _storageMock.Setup(s => s.IsDirectoryAsync("test.txt")).ReturnsAsync(false);
        _storageMock.Setup(s => s.ExistsAsync("test.txt")).ReturnsAsync(false);
        _storageMock.Setup(s => s.ToAbsolutePath("test.txt"))
            .Returns("/storage/test.txt");
        _storageMock.Setup(s => s.ReadAsync("test.txt", true))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3]));
        var sut = new FileService(
            _storageMock.Object, _aclMock.Object, search.Object, _shareId);
        var ctx = CreateContext();
        AllowAccess(FilePermission.CreateWriteData);

        await sut.WriteFileAsync(
                "test.txt", new MemoryStream([1, 2, 3]), ctx)
            .WaitAsync(TimeSpan.FromSeconds(1));

        search.Verify(s => s.onFileCreated(
            "/storage/test.txt",
            It.IsAny<Task<Stream>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        searchCompletion.SetResult();
    }

    // ═══════════════════ CreateFileAsync ═══════════════════

    [Fact]
    public async Task CreateFileAsync_WithAccess_WritesEmptyStream()
    {
        var ctx = CreateContext();
        AllowAccess(FilePermission.CreateWriteData);

        await _sut.CreateFileAsync("/test.txt", ctx);
        _storageMock.Verify(
            s => s.WriteAsync(It.IsAny<string>(), Stream.Null), Times.Once);
    }

    [Fact]
    public async Task CreateFileAsync_WithoutAccess_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        DenyAccess(FilePermission.CreateWriteData);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.CreateFileAsync("/test.txt", ctx));
    }

    // ═══════════════════ CreateDirectoryAsync ═══════════════════

    [Fact]
    public async Task CreateDirectoryAsync_WithAccess_CallsStorageCreateDirectory()
    {
        var ctx = CreateContext();
        AllowAccess(FilePermission.CreateWriteData);

        await _sut.CreateDirectoryAsync("/newdir", ctx);
        _storageMock.Verify(
            s => s.CreateDirectory(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task CreateDirectoryAsync_DoesNotWaitForSearchIndex()
    {
        var search = new Mock<ISearchService>();
        var searchCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        search.Setup(s => s.onDirectoryCreated("/storage/newdir"))
            .Returns(searchCompletion.Task);
        _storageMock.Setup(s => s.ToAbsolutePath("newdir"))
            .Returns("/storage/newdir");
        var sut = new FileService(
            _storageMock.Object, _aclMock.Object, search.Object, _shareId);
        var ctx = CreateContext();
        AllowAccess(FilePermission.CreateWriteData);

        await sut.CreateDirectoryAsync("/newdir", ctx)
            .WaitAsync(TimeSpan.FromSeconds(1));

        search.Verify(s => s.onDirectoryCreated("/storage/newdir"), Times.Once);
        searchCompletion.SetResult();
    }

    [Fact]
    public async Task CreateDirectoryAsync_WithoutAccess_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        DenyAccess(FilePermission.CreateWriteData);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.CreateDirectoryAsync("/newdir", ctx));
    }

    [Theory]
    [InlineData(".RECYCLE_BIN")]
    [InlineData(".kaimo-user-folder")]
    public async Task CreateDirectoryAsync_ReservedRootName_IsDeniedBeforeStorage(
        string path)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.CreateDirectoryAsync(path, CreateContext()));

        _storageMock.Verify(
            s => s.CreateDirectory(It.IsAny<string>()), Times.Never);
        _aclMock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(".RECYCLE_BIN_backup")]
    [InlineData("documents/.kaimo-notes")]
    public async Task CreateDirectoryAsync_SimilarOrNestedName_RemainsAllowed(
        string path)
    {
        AllowAccess(FilePermission.CreateWriteData);

        await _sut.CreateDirectoryAsync(path, CreateContext());

        _storageMock.Verify(s => s.CreateDirectory(path), Times.Once);
    }
}
