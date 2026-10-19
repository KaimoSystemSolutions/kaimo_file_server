using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Search;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Verifies that every creation choke point in <see cref="FileService"/> records the
/// creating user as the owner. Both transports funnel through here: the web upload path
/// uses <c>WriteFileAsync</c>/<c>CreateDirectoryAsync</c>, the SMB transport uses
/// <c>OpenAsync</c> (create-via-open) and <c>CreateDirectoryAsync</c>. Ownership is set
/// only on a fresh create — never reassigned on overwrite — and is best-effort, so a
/// failure must never fail the underlying file operation.
/// </summary>
public class FileServiceOwnershipTests
{
    private readonly Mock<IStorageEngine> _storageMock = new();
    private readonly Mock<IAclService> _aclMock = new();
    private readonly Mock<IFileOwnershipService> _ownershipMock = new();
    private readonly Guid _shareId = Guid.NewGuid();
    private readonly FileService _sut;

    public FileServiceOwnershipTests()
    {
        // versionService = null, ownershipService = the mock we assert against.
        _sut = new FileService(
            _storageMock.Object, _aclMock.Object, null, _shareId, null, _ownershipMock.Object);
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

    // ═══════════════════ CreateDirectoryAsync ═══════════════════

    [Fact]
    public async Task CreateDirectoryAsync_RecordsCreatorAsOwner()
    {
        var ctx = CreateContext();
        AllowAccess(FilePermission.CreateWriteData);

        await _sut.CreateDirectoryAsync("/newdir", ctx);

        _ownershipMock.Verify(o => o.EnsureOwnerAsync(
            _shareId, "newdir", true, ctx.User.Id), Times.Once);
    }

    // ═══════════════════ CreateFileAsync ═══════════════════

    [Fact]
    public async Task CreateFileAsync_RecordsCreatorAsOwner()
    {
        var ctx = CreateContext();
        AllowAccess(FilePermission.CreateWriteData);

        await _sut.CreateFileAsync("/empty.txt", ctx);

        _ownershipMock.Verify(o => o.EnsureOwnerAsync(
            _shareId, "empty.txt", false, ctx.User.Id), Times.Once);
    }

    // ═══════════════════ WriteFileAsync (web upload) ═══════════════════

    [Fact]
    public async Task WriteFileAsync_NewFile_RecordsCreatorAsOwner()
    {
        var ctx = CreateContext();
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storageMock.Setup(s => s.ExistsAsync(It.IsAny<string>())).ReturnsAsync(false); // brand new
        AllowAccess(FilePermission.CreateWriteData);

        await _sut.WriteFileAsync("/report.txt", new MemoryStream([1, 2, 3]), ctx);

        _ownershipMock.Verify(o => o.EnsureOwnerAsync(
            _shareId, "report.txt", false, ctx.User.Id), Times.Once);
    }

    [Fact]
    public async Task WriteFileAsync_OverwriteExisting_DoesNotReassignOwner()
    {
        var ctx = CreateContext();
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storageMock.Setup(s => s.ExistsAsync(It.IsAny<string>())).ReturnsAsync(true); // already exists
        AllowAccess(FilePermission.CreateWriteData);

        await _sut.WriteFileAsync("/report.txt", new MemoryStream([1, 2, 3]), ctx);

        _ownershipMock.Verify(o => o.EnsureOwnerAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Guid>()), Times.Never);
    }

    // ═══════════════════ OpenAsync (SMB create-via-open) ═══════════════════

    [Fact]
    public async Task OpenAsync_CreatesNewFile_RecordsCreatorAsOwner()
    {
        var ctx = CreateContext();
        _storageMock.Setup(s => s.ExistsAsync(It.IsAny<string>())).ReturnsAsync(false); // create
        _storageMock
            .Setup(s => s.OpenAsync(
                It.IsAny<string>(), It.IsAny<OpenMode>(), It.IsAny<AccessIntent>(),
                It.IsAny<ShareIntent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IStorageHandle>(h => h.IsDirectory == false));
        AllowAccess(FilePermission.CreateWriteData);

        await _sut.OpenAsync(
            "/new.txt", OpenMode.Create, AccessIntent.ReadWrite, ShareIntent.Write, ctx);

        _ownershipMock.Verify(o => o.EnsureOwnerAsync(
            _shareId, "new.txt", false, ctx.User.Id), Times.Once);
    }

    [Fact]
    public async Task OpenAsync_OpensExistingFile_DoesNotRecordOwner()
    {
        var ctx = CreateContext();
        _storageMock.Setup(s => s.ExistsAsync(It.IsAny<string>())).ReturnsAsync(true);
        _storageMock.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storageMock
            .Setup(s => s.OpenAsync(
                It.IsAny<string>(), It.IsAny<OpenMode>(), It.IsAny<AccessIntent>(),
                It.IsAny<ShareIntent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IStorageHandle>(h => h.IsDirectory == false));
        AllowAccess(FilePermission.CreateWriteData);
        AllowAccess(FilePermission.ListReadData);

        await _sut.OpenAsync(
            "/existing.txt", OpenMode.Open, AccessIntent.Read, ShareIntent.Read, ctx);

        _ownershipMock.Verify(o => o.EnsureOwnerAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Guid>()), Times.Never);
    }

    // ═══════════════════ Best-effort behaviour ═══════════════════

    [Fact]
    public async Task OwnershipFailure_DoesNotFailTheCreate()
    {
        // Recording ownership is a best-effort side effect: if it throws, the directory
        // creation itself must still succeed.
        var ctx = CreateContext();
        AllowAccess(FilePermission.CreateWriteData);
        _ownershipMock
            .Setup(o => o.EnsureOwnerAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Guid>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        await _sut.CreateDirectoryAsync("/newdir", ctx);

        _storageMock.Verify(s => s.CreateDirectory(It.IsAny<string>()), Times.Once);
    }

    // ═══════════════════ Not configured (optional dependency) ═══════════════════

    [Fact]
    public async Task NoOwnershipService_CreateStillSucceeds()
    {
        // Ownership tracking is an optional dependency; a FileService built without it
        // must keep working (the SMB/web factories always supply one, but unit setups may not).
        var sut = new FileService(_storageMock.Object, _aclMock.Object, null, _shareId);
        var ctx = CreateContext();
        AllowAccess(FilePermission.CreateWriteData);

        await sut.CreateDirectoryAsync("/newdir", ctx);

        _storageMock.Verify(s => s.CreateDirectory(It.IsAny<string>()), Times.Once);
    }
}
