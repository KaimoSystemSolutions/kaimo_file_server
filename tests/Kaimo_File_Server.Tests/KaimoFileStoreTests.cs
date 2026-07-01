using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Smb;
using Moq;
using Smb.FileSystem;
using Smb.Protocol.Enums;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Tests for the SMB transport adapter (<see cref="KaimoFileStore"/>) over the self-written SMB
/// library. The store implements the library's <see cref="IFileStore"/> and delegates every
/// operation to a mocked <see cref="IFileService"/> / <see cref="IFileSession"/> — no real disk and
/// no real SMB server.
///
/// The emphasis is the security contract (no caller / unknown user / read-only / missing delete
/// permission ⇒ denied) and the delegation path. The authenticated user is supplied exactly as at
/// runtime: the username via the ambient <see cref="SmbCaller"/>, resolved to a full
/// <see cref="UserContext"/> through <see cref="KaimoUserRegistry"/>.
/// </summary>
[Collection("KaimoUserRegistrySerial")]
public class KaimoFileStoreTests
{
    private readonly Mock<IFileService> _fileService = new();
    private readonly KaimoFileStore _sut;
    private readonly UserContext _user;
    private const string Username = "testuser";

    public KaimoFileStoreTests()
    {
        var user = new User(Guid.NewGuid(), "Test User", Username, "hash", "nthash");
        _user = new UserContext(user, [], [], []);

        // Isolate from any static registry state left by other tests: real clock, long TTL, no DI
        // (fresh entries are served from cache without needing re-resolution).
        KaimoUserRegistry.ResetForTests(TimeSpan.FromMinutes(5), () => DateTime.UtcNow, null);

        // The store resolves the caller (username) to this context through the registry.
        KaimoUserRegistry.Register(_user);

        _sut = new KaimoFileStore(Guid.NewGuid(), _fileService.Object);
    }

    // ───────────────────────── helpers ─────────────────────────

    /// <summary>Sets the ambient SMB caller for CREATE / snapshot operations.</summary>
    private static void AsUser(string userName) => SmbCaller.Current = new CallerInfo("WORKGROUP", userName);

    private Mock<IFileSession> NewSession(
        bool isDirectory = false, bool isReadOnly = false,
        string relativePath = "file.txt", long length = 5)
    {
        var s = new Mock<IFileSession>();
        s.SetupGet(x => x.IsDirectory).Returns(isDirectory);
        s.SetupGet(x => x.IsReadOnly).Returns(isReadOnly);
        s.SetupGet(x => x.RelativePath).Returns(relativePath);
        s.SetupGet(x => x.AbsolutePath).Returns($@"C:\storage\{relativePath}");
        s.SetupGet(x => x.Length).Returns(length);
        s.SetupGet(x => x.User).Returns(_user);
        s.Setup(x => x.FlushAsync(It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);
        s.Setup(x => x.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return s;
    }

    private void SetupOpen(Mock<IFileSession> session, FileOpenStatus status = FileOpenStatus.Opened)
        => _fileService
            .Setup(f => f.OpenAsync(
                It.IsAny<string>(), It.IsAny<OpenMode>(), It.IsAny<AccessIntent>(),
                It.IsAny<ShareIntent>(), It.IsAny<UserContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileOpenResult(session.Object, status));

    private IFileHandle OpenFileHandle(Mock<IFileSession> session)
    {
        AsUser(Username);
        SetupOpen(session);
        var result = _sut.Create("file.txt", FileAccessIntent.Read, CreateDispositionIntent.Open,
            directoryRequired: false, nonDirectoryRequired: true, out _);
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    // ═══════════════════ Security: caller resolution ═══════════════════

    [Fact]
    public void Create_NoCaller_DeniesAccess()
    {
        SmbCaller.Current = null;

        var result = _sut.Create("file.txt", FileAccessIntent.Read, CreateDispositionIntent.Open,
            false, true, out _);

        Assert.Equal(NtStatus.AccessDenied, result.Status);
        _fileService.Verify(f => f.OpenAsync(
            It.IsAny<string>(), It.IsAny<OpenMode>(), It.IsAny<AccessIntent>(),
            It.IsAny<ShareIntent>(), It.IsAny<UserContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void Create_UnknownUser_DeniesAccess()
    {
        AsUser("not_registered");

        var result = _sut.Create("file.txt", FileAccessIntent.Read, CreateDispositionIntent.Open,
            false, true, out _);

        Assert.Equal(NtStatus.AccessDenied, result.Status);
        _fileService.Verify(f => f.OpenAsync(
            It.IsAny<string>(), It.IsAny<OpenMode>(), It.IsAny<AccessIntent>(),
            It.IsAny<ShareIntent>(), It.IsAny<UserContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ═══════════════════ Delegation happy path ═══════════════════

    [Fact]
    public void Create_KnownUser_OpensViaFileService()
    {
        AsUser(Username);
        var session = NewSession();
        SetupOpen(session, FileOpenStatus.Opened);

        var result = _sut.Create("file.txt", FileAccessIntent.Read, CreateDispositionIntent.Open,
            false, true, out var outcome);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(CreateOutcome.Opened, outcome);
        _fileService.Verify(f => f.OpenAsync(
            It.IsAny<string>(), It.IsAny<OpenMode>(), It.IsAny<AccessIntent>(),
            It.IsAny<ShareIntent>(), It.IsAny<UserContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void Create_NewFile_ReportsCreatedOutcome()
    {
        AsUser(Username);
        var session = NewSession();
        SetupOpen(session, FileOpenStatus.Created);

        _sut.Create("new.txt", FileAccessIntent.Write, CreateDispositionIntent.Create,
            false, true, out var outcome);

        Assert.Equal(CreateOutcome.Created, outcome);
    }

    [Fact]
    public void Read_DelegatesToSession()
    {
        var session = NewSession();
        session.Setup(x => x.ReadAsync(It.IsAny<long>(), It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<int>(3));
        var handle = OpenFileHandle(session);

        var buffer = new byte[10];
        var result = _sut.Read(handle, 0, buffer);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value);
    }

    [Fact]
    public void Read_AtEnd_ReturnsZero()
    {
        var session = NewSession();
        session.Setup(x => x.ReadAsync(It.IsAny<long>(), It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<int>(0));
        var handle = OpenFileHandle(session);

        var result = _sut.Read(handle, 0, new byte[10]);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value); // dispatcher maps 0 → STATUS_END_OF_FILE
    }

    [Fact]
    public void Write_DelegatesToSession()
    {
        var session = NewSession();
        session.Setup(x => x.WriteAsync(It.IsAny<long>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        var handle = OpenFileHandle(session);

        var result = _sut.Write(handle, 0, new byte[] { 1, 2, 3, 4 });

        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.Value);
    }

    [Fact]
    public void Flush_DelegatesToSession()
    {
        var session = NewSession();
        var handle = OpenFileHandle(session);

        Assert.Equal(NtStatus.Success, _sut.Flush(handle));
        session.Verify(x => x.FlushAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    // ═══════════════════ Security: read-only & delete-on-close ═══════════════════

    [Fact]
    public void Write_ReadOnlySession_DeniesAccess()
    {
        var session = NewSession(isReadOnly: true);
        var handle = OpenFileHandle(session);

        var result = _sut.Write(handle, 0, new byte[] { 1 });

        Assert.Equal(NtStatus.AccessDenied, result.Status);
        session.Verify(
            x => x.WriteAsync(It.IsAny<long>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void SetDeleteOnClose_WithoutDeletePermission_DeniesAccess()
    {
        var session = NewSession();
        _fileService.Setup(f => f.CanDeleteAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(false);
        var handle = OpenFileHandle(session);

        var status = _sut.SetDeleteOnClose(handle, true);

        Assert.Equal(NtStatus.AccessDenied, status);
        session.Verify(x => x.MarkDeleteOnClose(), Times.Never);
    }

    [Fact]
    public void SetDeleteOnClose_WithDeletePermission_MarksSession()
    {
        var session = NewSession();
        _fileService.Setup(f => f.CanDeleteAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(true);
        var handle = OpenFileHandle(session);

        var status = _sut.SetDeleteOnClose(handle, true);

        Assert.Equal(NtStatus.Success, status);
        session.Verify(x => x.MarkDeleteOnClose(), Times.Once);
    }

    [Fact]
    public void Dispose_DisposesSession()
    {
        var session = NewSession();
        var handle = OpenFileHandle(session);

        handle.Dispose();

        session.Verify(x => x.DisposeAsync(), Times.Once);
    }

    // ═══════════════════ QueryDirectory ═══════════════════

    [Fact]
    public void QueryDirectory_ListsChildrenPlusDotEntries()
    {
        AsUser(Username);
        var dir = NewSession(isDirectory: true, relativePath: "dir");
        SetupOpen(dir);
        _fileService.Setup(f => f.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(new List<FileMetadata>
            {
                new() { Name = "a.txt", IsDirectory = false, Size = 10,
                        CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow },
                new() { Name = "sub", IsDirectory = true,
                        CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow },
            });

        var open = _sut.Create("dir", FileAccessIntent.Read, CreateDispositionIntent.Open,
            directoryRequired: true, nonDirectoryRequired: false, out _);
        var result = _sut.QueryDirectory(open.Value!, "*");

        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.Value!.Count); // "." + ".." + two children
    }

    [Fact]
    public void QueryDirectory_OnFileHandle_ReturnsInvalidParameter()
    {
        var handle = OpenFileHandle(NewSession(isDirectory: false));

        var result = _sut.QueryDirectory(handle, "*");

        Assert.Equal(NtStatus.InvalidParameter, result.Status);
    }

    // ═══════════════════ GetInfo ═══════════════════

    [Fact]
    public void GetInfo_MapsMetadataFromFileService()
    {
        var session = NewSession(length: 42);
        _fileService.Setup(f => f.GetMetadataAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(new FileMetadata
            {
                Name = "file.txt", IsDirectory = false, Size = 7,
                CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow,
            });
        var handle = OpenFileHandle(session);

        FileEntryInfo info = handle.GetInfo();

        Assert.Equal("file.txt", info.Name);
        Assert.Equal(42, info.EndOfFile); // live size from the open session, not the cached metadata
        Assert.False(info.IsDirectory);
    }

    // ═══════════════════ Snapshots (ISnapshotStore) ═══════════════════

    [Fact]
    public void GetSnapshots_ReturnsTimestampsFromFileService()
    {
        AsUser(Username);
        var times = new List<DateTime> { new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), new(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc) };
        _fileService.Setup(f => f.GetSnapshotTimestampsAsync(It.IsAny<UserContext>())).ReturnsAsync(times);

        Assert.Equal(2, _sut.GetAllSnapshots().Count);
        Assert.Equal(2, _sut.GetSnapshots("any").Count);
    }

    [Fact]
    public void Create_SnapshotPath_OpensViaSnapshotService()
    {
        AsUser(Username);
        var session = NewSession(relativePath: "file.txt");
        _fileService.Setup(f => f.OpenSnapshotAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<UserContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);

        var result = _sut.Create(@"@GMT-2026.06.24-10.30.00\file.txt",
            FileAccessIntent.Read, CreateDispositionIntent.Open, false, true, out _);

        Assert.True(result.IsSuccess);
        _fileService.Verify(f => f.OpenSnapshotAsync(
            "file.txt", It.IsAny<DateTime>(), It.IsAny<UserContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ═══════════════════ Volume info (IVolumeInfoProvider) ═══════════════════

    [Fact]
    public void GetVolumeInfo_ReportsKaimoLabel()
    {
        _fileService.Setup(f => f.ToAbsolutePath(It.IsAny<string>())).Returns(Path.GetTempPath());

        VolumeInfo info = _sut.GetVolumeInfo();

        Assert.Equal("KaimoSMB", info.Label);
        Assert.Equal(0x12345678u, info.SerialNumber);
    }
}
