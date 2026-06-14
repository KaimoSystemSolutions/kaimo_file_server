using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Storage;
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
        _sut = new FileService(_storageMock.Object, _aclMock.Object, _shareId);
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
                ctx, _shareId, It.IsAny<List<(string, bool)>>(), FilePermission.ListReadData))
            .ReturnsAsync((UserContext _, Guid _, List<(string Path, bool IsDir)> entries, FilePermission _) =>
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