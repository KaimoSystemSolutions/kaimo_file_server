using Kaimo_File_Server_Core.Core.Domain;
using Kaimo_File_Server_Core.Core.Domain.Identity;
using Kaimo_File_Server_Core.Core.Security;
using Kaimo_File_Server_Core.Core.Services;
using Kaimo_File_Server_Core.Core.Storage;
using Moq;
using Xunit;

namespace Kaimo_File_Server_Core.Tests;

public class FileServiceTests
{
    private readonly Mock<IStorageEngine> _storageMock = new();
    private readonly Mock<IAclService> _aclMock = new();
    private readonly FileService _sut;

    public FileServiceTests()
    {
        _sut = new FileService(_storageMock.Object, _aclMock.Object);
    }

    private static UserContext CreateContext()
    {
        var user = new User(Guid.NewGuid(), "Test", "test", "hash", "nt");
        return new UserContext(user, [], [], []);
    }

    private static FileMetadata CreateMeta(string path = "/test.txt")
        => new()
        {
            Id = Guid.NewGuid(),
            Path = path,
            Name = "test.txt",
            Size = 0,
            IsDirectory = false,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            Acl = []
        };

    // ========== CanReadAsync ==========

    [Fact]
    public async Task CanReadAsync_WithAccess_ReturnsTrue()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.ListReadData)).Returns(true);

        var result = await _sut.CanReadAsync("/test.txt", ctx);
        Assert.True(result);
    }

    [Fact]
    public async Task CanReadAsync_WithoutAccess_ReturnsFalse()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.ListReadData)).Returns(false);

        var result = await _sut.CanReadAsync("/test.txt", ctx);
        Assert.False(result);
    }

    // ========== CanWriteAsync ==========

    [Fact]
    public async Task CanWriteAsync_WithAccess_ReturnsTrue()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.CreateWriteData)).Returns(true);

        var result = await _sut.CanWriteAsync("/test.txt", ctx);
        Assert.True(result);
    }

    // ========== CanDeleteAsync ==========

    [Fact]
    public async Task CanDeleteAsync_WithAccess_ReturnsTrue()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.Delete)).Returns(true);

        var result = await _sut.CanDeleteAsync("/test.txt", ctx);
        Assert.True(result);
    }

    // ========== ReadFileAsync ==========

    [Fact]
    public async Task ReadFileAsync_WithAccess_ReturnsStream()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        var expectedStream = new MemoryStream([1, 2, 3]);

        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.ListReadData)).Returns(true);
        _storageMock.Setup(s => s.ReadAsync(It.IsAny<string>())).ReturnsAsync(expectedStream);

        var result = await _sut.ReadFileAsync("/test.txt", ctx);

        Assert.Same(expectedStream, result);
    }

    [Fact]
    public async Task ReadFileAsync_WithoutAccess_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.ListReadData)).Returns(false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.ReadFileAsync("/test.txt", ctx));
    }

    // ========== WriteFileAsync ==========

    [Fact]
    public async Task WriteFileAsync_WithAccess_CallsStorageWrite()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        var data = new MemoryStream([1, 2, 3]);

        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.CreateWriteData)).Returns(true);

        await _sut.WriteFileAsync("/test.txt", data, ctx);

        _storageMock.Verify(s => s.WriteAsync("/test.txt", data), Times.Once);
    }

    [Fact]
    public async Task WriteFileAsync_WithoutAccess_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.CreateWriteData)).Returns(false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.WriteFileAsync("/test.txt", Stream.Null, ctx));
    }

    // ========== DeleteFileAsync ==========

    [Fact]
    public async Task DeleteFileAsync_WithAccess_CallsStorageDelete()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();

        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.Delete)).Returns(true);

        await _sut.DeleteFileAsync("/test.txt", ctx);

        _storageMock.Verify(s => s.DeleteAsync("/test.txt"), Times.Once);
    }

    [Fact]
    public async Task DeleteFileAsync_WithoutAccess_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.Delete)).Returns(false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.DeleteFileAsync("/test.txt", ctx));
    }

    // ========== GetMetadataAsync ==========

    [Fact]
    public async Task GetMetadataAsync_WithAccess_ReturnsMeta()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.ListReadData)).Returns(true);

        var result = await _sut.GetMetadataAsync("/test.txt", ctx);

        Assert.Same(meta, result);
    }

    [Fact]
    public async Task GetMetadataAsync_WithoutAccess_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.ListReadData)).Returns(false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.GetMetadataAsync("/test.txt", ctx));
    }

    // ========== CreateFileAsync ==========

    [Fact]
    public async Task CreateFileAsync_WithAccess_WritesEmptyStream()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.CreateWriteData)).Returns(true);

        await _sut.CreateFileAsync("/test.txt", ctx);

        _storageMock.Verify(s => s.WriteAsync("/test.txt", Stream.Null), Times.Once);
    }

    [Fact]
    public async Task CreateFileAsync_WithoutAccess_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.CreateWriteData)).Returns(false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.CreateFileAsync("/test.txt", ctx));
    }
}