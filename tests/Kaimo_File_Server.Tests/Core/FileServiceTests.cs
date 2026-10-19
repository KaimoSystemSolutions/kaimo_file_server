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

    [Theory]
    [InlineData(".RECYCLE_BIN")]
    [InlineData(".RECYCLE_BIN/upload.txt")]
    [InlineData(".kaimo-close-captures")]
    public async Task CanCreateAsync_ReservedNamespace_ReturnsFalseWithoutAcl(
        string path)
    {
        Assert.False(await _sut.CanCreateAsync(path, CreateContext()));
        _aclMock.VerifyNoOtherCalls();
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
