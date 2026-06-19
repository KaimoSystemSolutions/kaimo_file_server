using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Smb;
using Moq;
using SMBLibrary;
using Xunit;
using FileAttributes = SMBLibrary.FileAttributes;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Tests for the SMB transport adapter (<see cref="SmbFileSystem"/>) against the
/// CURRENT design:
///   • the constructor takes only (shareName, IFileService) — no root path,
///   • every operation is delegated to <see cref="IFileService"/> / <see cref="IFileSession"/>,
///   • the user context is resolved from the SMB <c>SecurityContext.UserName</c>
///     via a static username cache (<see cref="SmbFileSystem.RegisterUser"/>).
///
/// These are interaction tests over a mocked IFileService — no real disk and no
/// real SMB server. The emphasis is the security contract (no user / read-only /
/// missing delete permission / invalid handle => denied) and the delegation path.
///
/// The old disk-based tests were dropped: they targeted a previous architecture
/// (3-arg constructor + per-async-flow SetSessionUser + direct filesystem access)
/// that no longer exists.
/// </summary>
public class SmbFileSystemTests
{
    private readonly Mock<IFileService> _fileService = new();
    private readonly SmbFileSystem _sut;
    private readonly UserContext _user;
    private const string Username = "testuser";

    public SmbFileSystemTests()
    {
        var user = new User(Guid.NewGuid(), "Test User", Username, "hash", "nthash");
        _user = new UserContext(user, [], [], []);

        // The adapter resolves the user from SecurityContext.UserName through this
        // static cache, so the user must be registered before any CreateFile call.
        SmbFileSystem.RegisterUser(_user);

        _sut = new SmbFileSystem("testshare", _fileService.Object);
    }

    // ───────────────────────── helpers ─────────────────────────

    /// <summary>SMBLibrary SecurityContext carrying just the username we need.</summary>
    private static SecurityContext Ctx(string? userName)
        => new(userName!, "MACHINE", null, null, null);

    private Mock<IFileSession> NewSession(
        bool isDirectory = false, bool isReadOnly = false,
        string relativePath = "file.txt", long length = 5)
    {
        var s = new Mock<IFileSession>();
        s.SetupGet(x => x.IsDirectory).Returns(isDirectory);
        s.SetupGet(x => x.IsReadOnly).Returns(isReadOnly);
        s.SetupGet(x => x.RelativePath).Returns(relativePath);
        s.SetupGet(x => x.Length).Returns(length);
        s.SetupGet(x => x.User).Returns(_user);
        s.Setup(x => x.FlushAsync(It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);
        s.Setup(x => x.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return s;
    }

    private void SetupOpen(Mock<IFileSession> session, FileOpenStatus status = FileOpenStatus.Opened)
    {
        _fileService
            .Setup(f => f.OpenAsync(
                It.IsAny<string>(), It.IsAny<OpenMode>(), It.IsAny<AccessIntent>(),
                It.IsAny<ShareIntent>(), It.IsAny<UserContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileOpenResult(session.Object, status));
    }

    private object OpenFileHandle(Mock<IFileSession> session)
    {
        SetupOpen(session);
        var status = _sut.CreateFile(out var handle, out _, session.Object.RelativePath,
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, Ctx(Username));
        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        return handle;
    }

    private object OpenDirectoryHandle(Mock<IFileSession> session)
    {
        SetupOpen(session);
        var status = _sut.CreateFile(out var handle, out _, session.Object.RelativePath,
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, Ctx(Username));
        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        return handle;
    }

    // ═══════════════════ User cache (ABE lookup) ═══════════════════

    [Fact]
    public void RegisterUser_ThenLookup_ReturnsSameContext()
        => Assert.Same(_user, SmbFileSystem.LookupUser(Username));

    [Fact]
    public void LookupUser_Unknown_ReturnsNull()
        => Assert.Null(SmbFileSystem.LookupUser("nobody_" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void LookupUser_Null_ReturnsNull()
        => Assert.Null(SmbFileSystem.LookupUser(null));

    // ═══════════════════ Security: user resolution ═══════════════════

    [Fact]
    public void CreateFile_NoSecurityContext_DeniesAccess()
    {
        var status = _sut.CreateFile(out var handle, out _, "file.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null!);

        Assert.Equal(NTStatus.STATUS_ACCESS_DENIED, status);
        Assert.Null(handle);
    }

    [Fact]
    public void CreateFile_UnregisteredUser_DeniesAccess()
    {
        var status = _sut.CreateFile(out _, out _, "file.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            Ctx("not_registered"));

        Assert.Equal(NTStatus.STATUS_ACCESS_DENIED, status);
        _fileService.Verify(f => f.OpenAsync(
            It.IsAny<string>(), It.IsAny<OpenMode>(), It.IsAny<AccessIntent>(),
            It.IsAny<ShareIntent>(), It.IsAny<UserContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ═══════════════════ Delegation happy path ═══════════════════

    [Fact]
    public void CreateFile_KnownUser_OpensViaFileService()
    {
        var session = NewSession();
        SetupOpen(session, FileOpenStatus.Opened);

        var status = _sut.CreateFile(out var handle, out var fileStatus, "file.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, Ctx(Username));

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        Assert.NotNull(handle);
        Assert.Equal(FileStatus.FILE_OPENED, fileStatus);
        _fileService.Verify(f => f.OpenAsync(
            It.IsAny<string>(), It.IsAny<OpenMode>(), It.IsAny<AccessIntent>(),
            It.IsAny<ShareIntent>(), It.IsAny<UserContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void CreateFile_NewFile_ReportsCreatedStatus()
    {
        var session = NewSession();
        SetupOpen(session, FileOpenStatus.Created);

        _sut.CreateFile(out _, out var fileStatus, "new.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.None,
            CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE, Ctx(Username));

        Assert.Equal(FileStatus.FILE_CREATED, fileStatus);
    }

    [Fact]
    public void ReadFile_DelegatesToSession()
    {
        var session = NewSession();
        session.Setup(x => x.ReadAsync(It.IsAny<long>(), It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<int>(3));
        var handle = OpenFileHandle(session);

        var status = _sut.ReadFile(out var data, handle, 0, 10);

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        Assert.Equal(3, data.Length);
    }

    [Fact]
    public void ReadFile_AtEnd_ReturnsEndOfFile()
    {
        var session = NewSession();
        session.Setup(x => x.ReadAsync(It.IsAny<long>(), It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<int>(0));
        var handle = OpenFileHandle(session);

        Assert.Equal(NTStatus.STATUS_END_OF_FILE, _sut.ReadFile(out _, handle, 0, 10));
    }

    [Fact]
    public void WriteFile_DelegatesToSession()
    {
        var session = NewSession();
        session.Setup(x => x.WriteAsync(It.IsAny<long>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        var handle = OpenFileHandle(session);

        var status = _sut.WriteFile(out var written, handle, 0, new byte[] { 1, 2, 3, 4 });

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        Assert.Equal(4, written);
    }

    [Fact]
    public void CloseFile_DisposesSession()
    {
        var session = NewSession();
        var handle = OpenFileHandle(session);

        Assert.Equal(NTStatus.STATUS_SUCCESS, _sut.CloseFile(handle));
        session.Verify(x => x.DisposeAsync(), Times.Once);
    }

    // ═══════════════════ Security: read-only & delete-on-close ═══════════════════

    [Fact]
    public void WriteFile_ReadOnlySession_DeniesAccess()
    {
        var session = NewSession(isReadOnly: true);
        var handle = OpenFileHandle(session);

        var status = _sut.WriteFile(out _, handle, 0, new byte[] { 1 });

        Assert.Equal(NTStatus.STATUS_ACCESS_DENIED, status);
        session.Verify(
            x => x.WriteAsync(It.IsAny<long>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void CreateFile_DeleteOnClose_WithoutDeletePermission_DeniesAccess()
    {
        var session = NewSession();
        SetupOpen(session);
        _fileService.Setup(f => f.CanDeleteAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(false);

        var status = _sut.CreateFile(out _, out _, "file.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_DELETE_ON_CLOSE,
            Ctx(Username));

        Assert.Equal(NTStatus.STATUS_ACCESS_DENIED, status);
        session.Verify(x => x.MarkDeleteOnClose(), Times.Never);
    }

    // ═══════════════════ QueryDirectory ═══════════════════

    [Fact]
    public void QueryDirectory_ListsChildrenFromFileService()
    {
        var dir = NewSession(isDirectory: true, relativePath: "dir");
        _fileService.Setup(f => f.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(new List<FileMetadata>
            {
                new() { Name = "a.txt", IsDirectory = false, Size = 10,
                        CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow },
                new() { Name = "sub", IsDirectory = true,
                        CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow },
            });
        var handle = OpenDirectoryHandle(dir);

        var status = _sut.QueryDirectory(out var result, handle, "*",
            FileInformationClass.FileBothDirectoryInformation);

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        Assert.Equal(4, result.Count); // "." + ".." + the two children
    }

    [Fact]
    public void QueryDirectory_OnFileHandle_ReturnsInvalidHandle()
    {
        var handle = OpenFileHandle(NewSession(isDirectory: false));

        Assert.Equal(NTStatus.STATUS_INVALID_HANDLE,
            _sut.QueryDirectory(out _, handle, "*", FileInformationClass.FileBothDirectoryInformation));
    }

    // ═══════════════════ Invalid-handle contract ═══════════════════

    [Fact]
    public void ReadFile_InvalidHandle_ReturnsInvalidHandle()
        => Assert.Equal(NTStatus.STATUS_INVALID_HANDLE, _sut.ReadFile(out _, new object(), 0, 10));

    [Fact]
    public void WriteFile_InvalidHandle_ReturnsInvalidHandle()
        => Assert.Equal(NTStatus.STATUS_INVALID_HANDLE, _sut.WriteFile(out _, new object(), 0, new byte[] { 1 }));

    [Fact]
    public void FlushFileBuffers_InvalidHandle_ReturnsInvalidHandle()
        => Assert.Equal(NTStatus.STATUS_INVALID_HANDLE, _sut.FlushFileBuffers(new object()));

    [Fact]
    public void CloseFile_InvalidHandle_ReturnsInvalidHandle()
        => Assert.Equal(NTStatus.STATUS_INVALID_HANDLE, _sut.CloseFile(new object()));

    [Fact]
    public void GetFileInformation_InvalidHandle_ReturnsInvalidHandle()
        => Assert.Equal(NTStatus.STATUS_INVALID_HANDLE,
            _sut.GetFileInformation(out _, new object(), FileInformationClass.FileBasicInformation));

    [Fact]
    public void SetFileInformation_InvalidHandle_ReturnsInvalidHandle()
        => Assert.Equal(NTStatus.STATUS_INVALID_HANDLE, _sut.SetFileInformation(new object(), null!));

    // ═══════════════════ DeviceIOControl (snapshot enumeration) ═══════════════════

    [Fact]
    public void DeviceIOControl_UnsupportedCtlCode_ReturnsNotSupported()
        => Assert.Equal(NTStatus.STATUS_NOT_SUPPORTED,
            _sut.DeviceIOControl(new object(), 0x0u, Array.Empty<byte>(), out _, 1024));

    [Fact]
    public void DeviceIOControl_EnumerateSnapshots_InvalidHandle_ReturnsInvalidHandle()
        => Assert.Equal(NTStatus.STATUS_INVALID_HANDLE,
            _sut.DeviceIOControl(new object(),
                SmbSnapshotHandler.FSCTL_SRV_ENUMERATE_SNAPSHOTS,
                Array.Empty<byte>(), out _, 1024));

    // ═══════════════════ Stub operations ═══════════════════

    [Fact]
    public void Cancel_ReturnsSuccess()
        => Assert.Equal(NTStatus.STATUS_SUCCESS, _sut.Cancel(new object()));

    [Fact]
    public void LockFile_ReturnsSuccess()
        => Assert.Equal(NTStatus.STATUS_SUCCESS, _sut.LockFile(new object(), 0, 0, false));

    [Fact]
    public void UnlockFile_ReturnsSuccess()
        => Assert.Equal(NTStatus.STATUS_SUCCESS, _sut.UnlockFile(new object(), 0, 0));

    [Fact]
    public void NotifyChange_ReturnsNotSupported()
        => Assert.Equal(NTStatus.STATUS_NOT_SUPPORTED,
            _sut.NotifyChange(out _, new object(), default, false, 0, null!, null!));

    [Fact]
    public void SetFileSystemInformation_ReturnsNotSupported()
        => Assert.Equal(NTStatus.STATUS_NOT_SUPPORTED, _sut.SetFileSystemInformation(null!));

    [Fact]
    public void GetSecurityInformation_ReturnsSuccess()
        => Assert.Equal(NTStatus.STATUS_SUCCESS,
            _sut.GetSecurityInformation(out _, new object(), default));

    [Fact]
    public void SetSecurityInformation_ReturnsSuccess()
        => Assert.Equal(NTStatus.STATUS_SUCCESS,
            _sut.SetSecurityInformation(new object(), default, null!));
}
