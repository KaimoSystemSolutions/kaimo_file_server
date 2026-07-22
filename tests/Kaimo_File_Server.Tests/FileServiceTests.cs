using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Search;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public class FileServiceTests
{
    private readonly Mock<IStorageEngine> _storageMock = new();
    private readonly Mock<IAclService> _aclMock = new();
    private readonly Guid _shareId = Guid.NewGuid();
    private readonly FileService _sut;

    public FileServiceTests()
    {
        _sut = new FileService(_storageMock.Object, _aclMock.Object, null, _shareId);
    }

    private static UserContext CreateContext()
    {
        var user = new User(Guid.NewGuid(), "Test", "test", "hash", "nt");
        return new UserContext(user, [], [], []);
    }

    private void AllowAccess(FilePermission permission)
    {
        _aclMock
            .Setup(a => a.HasAccessAsync(
                It.IsAny<UserContext>(), _shareId,
                It.IsAny<string>(), It.IsAny<bool>(), permission))
            .ReturnsAsync(true);
    }

    private void DenyAccess(FilePermission permission)
    {
        _aclMock
            .Setup(a => a.HasAccessAsync(
                It.IsAny<UserContext>(), _shareId,
                It.IsAny<string>(), It.IsAny<bool>(), permission))
            .ReturnsAsync(false);
    }

    // ═══════════════════ CanReadAsync ═══════════════════

    [Fact]
    public async Task CanReadAsync_WithAccess_ReturnsTrue()
    {
        var ctx = CreateContext();
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        AllowAccess(FilePermission.ListReadData);

        Assert.True(await _sut.CanReadAsync("/test.txt", ctx));
    }

    [Fact]
    public async Task CanReadAsync_WithoutAccess_ReturnsFalse()
    {
        var ctx = CreateContext();
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        DenyAccess(FilePermission.ListReadData);

        Assert.False(await _sut.CanReadAsync("/test.txt", ctx));
    }

    // ═══════════════════ CanWriteAsync ═══════════════════

    [Fact]
    public async Task CanWriteAsync_WithAccess_ReturnsTrue()
    {
        var ctx = CreateContext();
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        AllowAccess(FilePermission.CreateWriteData);

        Assert.True(await _sut.CanWriteAsync("/test.txt", ctx));
    }

    [Fact]
    public async Task CanWriteAsync_WithoutAccess_ReturnsFalse()
    {
        var ctx = CreateContext();
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        DenyAccess(FilePermission.CreateWriteData);

        Assert.False(await _sut.CanWriteAsync("/test.txt", ctx));
    }

    // ═══════════════════ CanDeleteAsync ═══════════════════

    [Fact]
    public async Task CanDeleteAsync_WithAccess_ReturnsTrue()
    {
        var ctx = CreateContext();
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        AllowAccess(FilePermission.Delete);

        Assert.True(await _sut.CanDeleteAsync("/test.txt", ctx));
    }

    [Fact]
    public async Task CanDeleteAsync_WithoutAccess_ReturnsFalse()
    {
        var ctx = CreateContext();
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        DenyAccess(FilePermission.Delete);

        Assert.False(await _sut.CanDeleteAsync("/test.txt", ctx));
    }

    // ═══════════════════ CanCreateAsync ═══════════════════

    [Fact]
    public async Task CanCreateAsync_WithAccess_ReturnsTrue()
    {
        var ctx = CreateContext();
        AllowAccess(FilePermission.CreateWriteData);

        Assert.True(await _sut.CanCreateAsync("/dir/test.txt", ctx));
    }

    [Fact]
    public async Task CanCreateAsync_WithoutAccess_ReturnsFalse()
    {
        var ctx = CreateContext();
        DenyAccess(FilePermission.CreateWriteData);

        Assert.False(await _sut.CanCreateAsync("/dir/test.txt", ctx));
    }

    // ═══════════════════ CanListAsync ═══════════════════

    [Fact]
    public async Task CanListAsync_WithAccess_ReturnsTrue()
    {
        var ctx = CreateContext();
        AllowAccess(FilePermission.ListReadData);

        Assert.True(await _sut.CanListAsync("/dir", ctx));
    }

    [Fact]
    public async Task CanListAsync_WithoutAccess_ReturnsFalse()
    {
        var ctx = CreateContext();
        DenyAccess(FilePermission.ListReadData);

        Assert.False(await _sut.CanListAsync("/dir", ctx));
    }

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
        _storageMock.Setup(s => s.ReadAsync(It.IsAny<string>()))
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
        _storageMock.Setup(s => s.ReadAsync(It.IsAny<string>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3]));
        AllowAccess(FilePermission.CreateWriteData);

        await sut.WriteFileAsync("/test.txt", data, ctx);

        _storageMock.Verify(s => s.WriteAsync(
            It.IsAny<string>(), data, It.IsAny<CancellationToken>()), Times.Once);
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
        _storageMock.Setup(s => s.ReadAsync(It.IsAny<string>())).ReturnsAsync(indexedContent);
        _storageMock.Setup(s => s.ToAbsolutePath("test.txt")).Returns("/storage/test.txt");
        AllowAccess(FilePermission.CreateWriteData);

        await sut.WriteFileAsync("/test.txt", data, ctx);

        _storageMock.Verify(s => s.WriteAsync(
            "test.txt", data, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(indexedContent.CanRead);
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
    public async Task CreateDirectoryAsync_WithoutAccess_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        DenyAccess(FilePermission.CreateWriteData);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.CreateDirectoryAsync("/newdir", ctx));
    }

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
            _shareId, "doc.txt", ".RECYCLE_BIN/doc_20260719.txt"), Times.Once);
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
        versions.Verify(v => v.RenamePathAsync(_shareId, "old", "new"), Times.Once);
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

    // ═══════════════════ GetMetadataAsync ═══════════════════

    [Fact]
    public async Task GetMetadataAsync_WithAccess_ReturnsMeta()
    {
        var ctx = CreateContext();
        var meta = new FileMetadata
        {
            Id = Guid.NewGuid(),
            Path = "test.txt",
            Name = "test.txt",
            Size = 100,
            IsDirectory = false,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            Acl = []
        };
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        AllowAccess(FilePermission.ListReadData);

        Assert.Same(meta, await _sut.GetMetadataAsync("/test.txt", ctx));
    }

    [Fact]
    public async Task GetMetadataAsync_WithoutAccess_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        var meta = new FileMetadata
        {
            Id = Guid.NewGuid(),
            Path = "test.txt",
            Name = "test.txt",
            Size = 0,
            IsDirectory = false,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            Acl = []
        };
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        DenyAccess(FilePermission.ListReadData);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.GetMetadataAsync("/test.txt", ctx));
    }

    // ═══════════════════ ListAsync ═══════════════════

    [Fact]
    public async Task ListAsync_WithAccess_ReturnsVisibleItems()
    {
        var ctx = CreateContext();
        var items = new List<FileMetadata>
        {
            new() { Name = "visible.txt", IsDirectory = false, Path = "dir/visible.txt" },
            new() { Name = "hidden.txt", IsDirectory = false, Path = "dir/hidden.txt" },
        };
        _storageMock.Setup(s => s.ListAsync(It.IsAny<string>())).ReturnsAsync(items);

        // Allow listing the directory itself
        _aclMock
            .Setup(a => a.HasAccessAsync(ctx, _shareId, It.IsAny<string>(), true, FilePermission.ListReadData))
            .ReturnsAsync(true);

        // Allow visible.txt, deny hidden.txt
        _aclMock
            .Setup(a => a.HasAccessBatchAsync(
                ctx, _shareId, It.IsAny<IReadOnlyList<(string, bool)>>(), FilePermission.ListReadData))
            .ReturnsAsync((UserContext _, Guid _, IReadOnlyList<(string Path, bool IsDir)> entries, FilePermission _) =>
                entries.ToDictionary(e => e.Path, e => e.Path.Contains("visible")));

        var result = await _sut.ListAsync("dir", ctx);
        Assert.Single(result);
        Assert.Equal("visible.txt", result[0].Name);
    }

    [Fact]
    public async Task ListAsync_DirAccessDenied_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        _aclMock
            .Setup(a => a.HasAccessAsync(ctx, _shareId, It.IsAny<string>(), true, FilePermission.ListReadData))
            .ReturnsAsync(false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.ListAsync("dir", ctx));
    }

    // ═══════════════════ OpenAsync — per-intent ACL enforcement ═══════════════════

    private void SetupExistingFile()
    {
        _storageMock.Setup(s => s.ExistsAsync(It.IsAny<string>())).ReturnsAsync(true);
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storageMock
            .Setup(s => s.OpenAsync(
                It.IsAny<string>(), It.IsAny<OpenMode>(), It.IsAny<AccessIntent>(),
                It.IsAny<ShareIntent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IStorageHandle>(h => h.IsDirectory == false));
    }

    [Fact]
    public async Task OpenAsync_ReadWrite_WithWriteButNoRead_IsDenied()
    {
        // Regression: a user with write permission but WITHOUT read permission must not be
        // able to open a file ReadWrite (which would grant readable content). Write intent
        // used to skip the read check entirely.
        var ctx = CreateContext();
        SetupExistingFile();
        AllowAccess(FilePermission.CreateWriteData);
        DenyAccess(FilePermission.ListReadData);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.OpenAsync("/secret.txt", OpenMode.Open, AccessIntent.ReadWrite, ShareIntent.Read, ctx));
    }

    [Fact]
    public async Task OpenAsync_ReadWrite_WithBothPermissions_Succeeds()
    {
        var ctx = CreateContext();
        SetupExistingFile();
        AllowAccess(FilePermission.CreateWriteData);
        AllowAccess(FilePermission.ListReadData);

        var result = await _sut.OpenAsync(
            "/doc.txt", OpenMode.Open, AccessIntent.ReadWrite, ShareIntent.Read, ctx);

        Assert.NotNull(result.Session);
    }

    [Fact]
    public async Task OpenAsync_WriteOnly_WithWriteButNoRead_Succeeds()
    {
        // A pure write ("drop box") open must NOT require read permission.
        var ctx = CreateContext();
        SetupExistingFile();
        AllowAccess(FilePermission.CreateWriteData);
        DenyAccess(FilePermission.ListReadData);

        var result = await _sut.OpenAsync(
            "/upload.txt", OpenMode.Open, AccessIntent.Write, ShareIntent.Write, ctx);

        Assert.NotNull(result.Session);
    }

    [Fact]
    public async Task OpenAsync_ReadOnly_WithoutReadPermission_IsDenied()
    {
        var ctx = CreateContext();
        SetupExistingFile();
        DenyAccess(FilePermission.ListReadData);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.OpenAsync("/doc.txt", OpenMode.Open, AccessIntent.Read, ShareIntent.Read, ctx));
    }

    // ═══════════════════ Directory operations ═══════════════════

    [Fact]
    public async Task CanReadAsync_Directory_UsesIsDirectoryResult()
    {
        var ctx = CreateContext();
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(true);

        _aclMock
            .Setup(a => a.HasAccessAsync(ctx, _shareId, It.IsAny<string>(), true, FilePermission.ListReadData))
            .ReturnsAsync(true);

        Assert.True(await _sut.CanReadAsync("/mydir", ctx));

        // Verify IsDirectoryAsync was called and the result (true) was passed to HasAccessAsync
        _aclMock.Verify(
            a => a.HasAccessAsync(ctx, _shareId, It.IsAny<string>(), true, FilePermission.ListReadData),
            Times.Once);
    }
}
