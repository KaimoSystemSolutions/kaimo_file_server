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
/// library. The store implements the library's async <see cref="IFileStore"/> and delegates every
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

    private async Task<IFileHandle> OpenFileHandleAsync(Mock<IFileSession> session)
    {
        AsUser(Username);
        SetupOpen(session);
        var result = await _sut.CreateAsync("file.txt", FileAccessIntent.Read, CreateDispositionIntent.Open,
            directoryRequired: false, nonDirectoryRequired: true, default);
        Assert.True(result.IsSuccess);
        return result.Value.Handle;
    }

    // ═══════════════════ Security: caller resolution ═══════════════════

    [Fact]
    public async Task Create_NoCaller_DeniesAccess()
    {
        SmbCaller.Current = null;

        var result = await _sut.CreateAsync("file.txt", FileAccessIntent.Read, CreateDispositionIntent.Open,
            false, true, default);

        Assert.Equal(NtStatus.AccessDenied, result.Status);
        _fileService.Verify(f => f.OpenAsync(
            It.IsAny<string>(), It.IsAny<OpenMode>(), It.IsAny<AccessIntent>(),
            It.IsAny<ShareIntent>(), It.IsAny<UserContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Create_UnknownUser_DeniesAccess()
    {
        AsUser("not_registered");

        var result = await _sut.CreateAsync("file.txt", FileAccessIntent.Read, CreateDispositionIntent.Open,
            false, true, default);

        Assert.Equal(NtStatus.AccessDenied, result.Status);
        _fileService.Verify(f => f.OpenAsync(
            It.IsAny<string>(), It.IsAny<OpenMode>(), It.IsAny<AccessIntent>(),
            It.IsAny<ShareIntent>(), It.IsAny<UserContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ═══════════════════ Delegation happy path ═══════════════════

    [Fact]
    public async Task Create_KnownUser_OpensViaFileService()
    {
        AsUser(Username);
        var session = NewSession();
        SetupOpen(session, FileOpenStatus.Opened);

        var result = await _sut.CreateAsync("file.txt", FileAccessIntent.Read, CreateDispositionIntent.Open,
            false, true, default);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value.Handle);
        Assert.Equal(CreateOutcome.Opened, result.Value.Action);
        _fileService.Verify(f => f.OpenAsync(
            It.IsAny<string>(), It.IsAny<OpenMode>(), It.IsAny<AccessIntent>(),
            It.IsAny<ShareIntent>(), It.IsAny<UserContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Create_NewFile_ReportsCreatedOutcome()
    {
        AsUser(Username);
        var session = NewSession();
        SetupOpen(session, FileOpenStatus.Created);

        var result = await _sut.CreateAsync("new.txt", FileAccessIntent.Write, CreateDispositionIntent.Create,
            false, true, default);

        Assert.Equal(CreateOutcome.Created, result.Value.Action);
    }

    [Fact]
    public async Task Read_DelegatesToSession()
    {
        var session = NewSession();
        session.Setup(x => x.ReadAsync(It.IsAny<long>(), It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<int>(3));
        var handle = await OpenFileHandleAsync(session);

        var buffer = new byte[10];
        var result = await _sut.ReadAsync(handle, 0, buffer, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value);
    }

    [Fact]
    public async Task Read_AtEnd_ReturnsZero()
    {
        var session = NewSession();
        session.Setup(x => x.ReadAsync(It.IsAny<long>(), It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<int>(0));
        var handle = await OpenFileHandleAsync(session);

        var result = await _sut.ReadAsync(handle, 0, new byte[10], default);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value); // dispatcher maps 0 → STATUS_END_OF_FILE
    }

    [Fact]
    public async Task Write_DelegatesToSession()
    {
        var session = NewSession();
        session.Setup(x => x.WriteAsync(It.IsAny<long>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        var handle = await OpenFileHandleAsync(session);

        var result = await _sut.WriteAsync(handle, 0, new byte[] { 1, 2, 3, 4 }, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.Value);
    }

    [Fact]
    public async Task Flush_DelegatesToSession()
    {
        var session = NewSession();
        var handle = await OpenFileHandleAsync(session);

        Assert.Equal(NtStatus.Success, await _sut.FlushAsync(handle, default));
        session.Verify(x => x.FlushAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    // ═══════════════════ Security: read-only & delete-on-close ═══════════════════

    [Fact]
    public async Task Write_ReadOnlySession_DeniesAccess()
    {
        var session = NewSession(isReadOnly: true);
        var handle = await OpenFileHandleAsync(session);

        var result = await _sut.WriteAsync(handle, 0, new byte[] { 1 }, default);

        Assert.Equal(NtStatus.AccessDenied, result.Status);
        session.Verify(
            x => x.WriteAsync(It.IsAny<long>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SetDeleteOnClose_WithoutDeletePermission_DeniesAccess()
    {
        var session = NewSession();
        _fileService.Setup(f => f.CanDeleteAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(false);
        var handle = await OpenFileHandleAsync(session);

        var status = await _sut.SetDeleteOnCloseAsync(handle, true, default);

        Assert.Equal(NtStatus.AccessDenied, status);
        session.Verify(x => x.MarkDeleteOnClose(), Times.Never);
    }

    [Fact]
    public async Task SetDeleteOnClose_WithDeletePermission_MarksSession()
    {
        var session = NewSession();
        _fileService.Setup(f => f.CanDeleteAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(true);
        var handle = await OpenFileHandleAsync(session);

        var status = await _sut.SetDeleteOnCloseAsync(handle, true, default);

        Assert.Equal(NtStatus.Success, status);
        session.Verify(x => x.MarkDeleteOnClose(), Times.Once);
    }

    [Fact]
    public async Task Dispose_DisposesSession()
    {
        var session = NewSession();
        var handle = await OpenFileHandleAsync(session);

        handle.Dispose();

        session.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_DisposesSession()
    {
        var session = NewSession();
        var handle = await OpenFileHandleAsync(session);

        await handle.DisposeAsync();

        session.Verify(x => x.DisposeAsync(), Times.Once);
    }

    // ═══════════════════ QueryDirectory ═══════════════════

    [Fact]
    public async Task QueryDirectory_ListsChildrenPlusDotEntries()
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

        var open = await _sut.CreateAsync("dir", FileAccessIntent.Read, CreateDispositionIntent.Open,
            directoryRequired: true, nonDirectoryRequired: false, default);
        var result = await _sut.QueryDirectoryAsync(open.Value.Handle, "*", default);

        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.Value!.Count); // "." + ".." + two children
    }

    [Fact]
    public async Task QueryDirectory_OnFileHandle_ReturnsInvalidParameter()
    {
        var handle = await OpenFileHandleAsync(NewSession(isDirectory: false));

        var result = await _sut.QueryDirectoryAsync(handle, "*", default);

        Assert.Equal(NtStatus.InvalidParameter, result.Status);
    }

    // ═══════════════════ GetInfo ═══════════════════

    [Fact]
    public async Task GetInfo_MapsMetadataFromFileService()
    {
        var session = NewSession(length: 42);
        _fileService.Setup(f => f.GetMetadataAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(new FileMetadata
            {
                Name = "file.txt", IsDirectory = false, Size = 7,
                CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow,
            });
        var handle = await OpenFileHandleAsync(session);

        FileEntryInfo info = handle.GetInfo();

        Assert.Equal("file.txt", info.Name);
        Assert.Equal(42, info.EndOfFile); // live size from the open session, not the cached metadata
        Assert.False(info.IsDirectory);
    }

    [Fact]
    public async Task GetInfoAsync_MapsMetadataFromFileService()
    {
        var session = NewSession(length: 42);
        _fileService.Setup(f => f.GetMetadataAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(new FileMetadata
            {
                Name = "file.txt", IsDirectory = false, Size = 7,
                CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow,
            });
        var handle = await OpenFileHandleAsync(session);

        FileEntryInfo info = await handle.GetInfoAsync(default);

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
    public async Task Create_SnapshotPath_OpensViaSnapshotService()
    {
        AsUser(Username);
        var session = NewSession(relativePath: "file.txt");
        _fileService.Setup(f => f.OpenSnapshotAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<UserContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);

        var result = await _sut.CreateAsync(@"@GMT-2026.06.24-10.30.00\file.txt",
            FileAccessIntent.Read, CreateDispositionIntent.Open, false, true, default);

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
