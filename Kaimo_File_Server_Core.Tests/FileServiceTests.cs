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

    // ========== CanRead ==========

    [Fact]
    public async Task CanRead_WithAccess_ReturnsTrue()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.ListReadData)).Returns(true);

        var result = await _sut.CanRead("/test.txt", ctx);
        Assert.True(result);
    }

    [Fact]
    public async Task CanRead_WithoutAccess_ReturnsFalse()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.ListReadData)).Returns(false);

        var result = await _sut.CanRead("/test.txt", ctx);
        Assert.False(result);
    }

    // ========== CanWrite ==========

    [Fact]
    public async Task CanWrite_WithAccess_ReturnsTrue()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.CreateWriteData)).Returns(true);

        var result = await _sut.CanWrite("/test.txt", ctx);
        Assert.True(result);
    }

    // ========== CanDelete ==========

    [Fact]
    public async Task CanDelete_WithAccess_ReturnsTrue()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.Delete)).Returns(true);

        var result = await _sut.CanDelete("/test.txt", ctx);
        Assert.True(result);
    }

    // ========== ReadFile ==========

    [Fact]
    public async Task ReadFile_WithAccess_ReturnsStream()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        var expectedStream = new MemoryStream([1, 2, 3]);

        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.ListReadData)).Returns(true);
        _storageMock.Setup(s => s.ReadAsync(It.IsAny<string>())).ReturnsAsync(expectedStream);

        var result = await _sut.ReadFile("/test.txt", ctx);

        Assert.Same(expectedStream, result);
    }

    [Fact]
    public async Task ReadFile_WithoutAccess_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.ListReadData)).Returns(false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.ReadFile("/test.txt", ctx));
    }

    // ========== WriteFile ==========

    [Fact]
    public async Task WriteFile_WithAccess_CallsStorageWrite()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        var data = new MemoryStream([1, 2, 3]);

        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.CreateWriteData)).Returns(true);

        await _sut.WriteFile("/test.txt", data, ctx);

        _storageMock.Verify(s => s.WriteAsync("/test.txt", data), Times.Once);
    }

    [Fact]
    public async Task WriteFile_WithoutAccess_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.CreateWriteData)).Returns(false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.WriteFile("/test.txt", Stream.Null, ctx));
    }

    // ========== DeleteFile ==========

    [Fact]
    public async Task DeleteFile_WithAccess_CallsStorageDelete()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();

        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.Delete)).Returns(true);

        await _sut.DeleteFile("/test.txt", ctx);

        _storageMock.Verify(s => s.DeleteAsync("/test.txt"), Times.Once);
    }

    [Fact]
    public async Task DeleteFile_WithoutAccess_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.Delete)).Returns(false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.DeleteFile("/test.txt", ctx));
    }

    // ========== GetMetadata ==========

    [Fact]
    public async Task GetMetadata_WithAccess_ReturnsMeta()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.ListReadData)).Returns(true);

        var result = await _sut.GetMetadata("/test.txt", ctx);

        Assert.Same(meta, result);
    }

    [Fact]
    public async Task GetMetadata_WithoutAccess_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.ListReadData)).Returns(false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.GetMetadata("/test.txt", ctx));
    }

    // ========== CreateFile ==========

    [Fact]
    public async Task CreateFile_WithAccess_WritesEmptyStream()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.CreateWriteData)).Returns(true);

        await _sut.CreateFile("/test.txt", ctx);

        _storageMock.Verify(s => s.WriteAsync("/test.txt", Stream.Null), Times.Once);
    }

    [Fact]
    public async Task CreateFile_WithoutAccess_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        var meta = CreateMeta();
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _aclMock.Setup(a => a.HasAccess(ctx, meta, FilePermission.CreateWriteData)).Returns(false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.CreateFile("/test.txt", ctx));
    }
}
