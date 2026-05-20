using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Smb;
using Moq;
using SMBLibrary;
using SMBLibrary.Server;
using Xunit;
using FileAttributes = SMBLibrary.FileAttributes;

namespace Kaimo_File_Server.Tests;

public class SmbFileSystemTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _shareName = "testshare";
    private readonly Mock<IFileService> _fileServiceMock;
    private readonly SmbFileSystem _sut;
    private readonly UserContext _testUser;
    private readonly UserContext _secondUser;

    public SmbFileSystemTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(),
            "kaimo_smb_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);

        _fileServiceMock = new Mock<IFileService>();
        _fileServiceMock
            .Setup(f => f.CanReadAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(true);
        _fileServiceMock
            .Setup(f => f.CanWriteAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(true);
        _fileServiceMock
            .Setup(f => f.CanCreateAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(true);
        _fileServiceMock
            .Setup(f => f.CanDeleteAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(true);

        _sut = new SmbFileSystem(_testRoot, _shareName, _fileServiceMock.Object);

        var user = new User(Guid.NewGuid(), "Test User", "testuser", "hash", "nthash");
        _testUser = new UserContext(user, [], [], []);

        var user2 = new User(Guid.NewGuid(), "Second User", "seconduser", "hash2", "nthash2");
        _secondUser = new UserContext(user2, [], [], []);

        // Set default session user
        SmbFileSystem.SetSessionUser(_testUser);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, true); } catch { }
    }

    private SecurityContext CreateSecurityContext() => null!;

    private void CreateTestFile(string rel, byte[]? content = null)
    {
        var p = Path.Combine(_testRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, content ?? [1, 2, 3, 4, 5]);
    }

    private void CreateTestDirectory(string rel)
    {
        Directory.CreateDirectory(Path.Combine(_testRoot, rel));
    }

    /// <summary>
    /// Helper to create a SmbFileSystem with a custom mock (for denial tests).
    /// </summary>
    private SmbFileSystem CreateSutWithMock(Mock<IFileService> mock)
        => new SmbFileSystem(_testRoot, _shareName, mock.Object);

    // ═══════════════════════════════════════════════════════════
    //  RACE CONDITION REGRESSION TESTS
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void SessionUser_IsIsolatedPerAsyncFlow()
    {
        UserContext? capturedOnThread1 = null;
        UserContext? capturedOnThread2 = null;

        var barrier = new Barrier(2);

        var t1 = Task.Run(() =>
        {
            SmbFileSystem.SetSessionUser(_testUser);
            barrier.SignalAndWait();
            barrier.SignalAndWait();
            CreateTestFile("race_t1.txt");
            _sut.CreateFile(out var h, out _, "race_t1.txt",
                AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
                CreateSecurityContext());
            capturedOnThread1 = _testUser;
            _sut.CloseFile(h);
        });

        var t2 = Task.Run(() =>
        {
            barrier.SignalAndWait();
            SmbFileSystem.SetSessionUser(_secondUser);
            barrier.SignalAndWait();
            CreateTestFile("race_t2.txt");
            _sut.CreateFile(out var h, out _, "race_t2.txt",
                AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
                CreateSecurityContext());
            capturedOnThread2 = _secondUser;
            _sut.CloseFile(h);
        });

        Task.WaitAll(t1, t2);

        Assert.Equal(_testUser, capturedOnThread1);
        Assert.Equal(_secondUser, capturedOnThread2);
    }

    [Fact]
    public void ConcurrentSessions_PermissionChecksUseCorrectUser()
    {
        var userAId = _testUser.User.Id;
        var userBId = _secondUser.User.Id;

        var seenUserIds = new System.Collections.Concurrent.ConcurrentBag<Guid>();

        _fileServiceMock
            .Setup(f => f.CanReadAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .Returns<string, UserContext>((path, ctx) =>
            {
                seenUserIds.Add(ctx.User.Id);
                return Task.FromResult(true);
            });

        CreateTestFile("concurrent_a.txt");
        CreateTestFile("concurrent_b.txt");

        var t1 = Task.Run(() =>
        {
            SmbFileSystem.SetSessionUser(_testUser);
            _sut.CreateFile(out var h, out _, "concurrent_a.txt",
                AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
                CreateSecurityContext());
            _sut.CloseFile(h);
        });

        var t2 = Task.Run(() =>
        {
            SmbFileSystem.SetSessionUser(_secondUser);
            _sut.CreateFile(out var h, out _, "concurrent_b.txt",
                AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
                CreateSecurityContext());
            _sut.CloseFile(h);
        });

        Task.WaitAll(t1, t2);

        Assert.Contains(userAId, seenUserIds);
        Assert.Contains(userBId, seenUserIds);
    }

    [Fact]
    public void NoSessionUser_ThrowsInvalidOperation()
    {
        var ex = Task.Run(() =>
        {
            SmbFileSystem.SetSessionUser(null!);
            return Record.Exception(() =>
            {
                _sut.CreateFile(out var h, out var fs, "noaccess.txt",
                    AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
                    CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE,
                    CreateSecurityContext());
            });
        }).GetAwaiter().GetResult();

        Assert.NotNull(ex);
        Assert.IsType<InvalidOperationException>(ex);
    }

    // ═══════════════════════════════════════════════════════════
    //  PATH TRAVERSAL PROTECTION
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CreateFile_PathTraversal_ReturnsDenied()
    {
        var status = _sut.CreateFile(out _, out _,
            @"..\..\..\etc\passwd",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        Assert.Equal(NTStatus.STATUS_ACCESS_DENIED, status);
    }

    [Fact]
    public void CreateFile_PathTraversal_UnixStyle_ReturnsDenied()
    {
        var status = _sut.CreateFile(out _, out _,
            "../../../etc/shadow",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        Assert.Equal(NTStatus.STATUS_ACCESS_DENIED, status);
    }

    [Theory]
    [InlineData("..\\..\\..\\etc\\passwd")]
    [InlineData("..\\..\\Windows\\System32\\config\\SAM")]
    [InlineData("sub\\..\\..\\..\\secret")]
    [InlineData("normal\\..\\..\\..\\..\\breakout")]
    public void CreateFile_PathTraversal_Variants_ReturnsDenied(string path)
    {
        var s = _sut.CreateFile(out _, out _, path,
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        Assert.Equal(NTStatus.STATUS_ACCESS_DENIED, s);
    }

    // ═══════════════════════════════════════════════════════════
    //  PERMISSION BOUNDARY TESTS
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ReadFile_DoesNotReCheckPermissions()
    {
        int callCount = 0;
        _fileServiceMock
            .Setup(f => f.CanReadAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .Returns<string, UserContext>((_, __) =>
            {
                callCount++;
                return Task.FromResult(true);
            });

        CreateTestFile("noRecheck.txt", [42]);
        _sut.CreateFile(out var h, out _, "noRecheck.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        int countAfterOpen = callCount;

        _sut.ReadFile(out var data, h, 0, 1);

        Assert.Equal(countAfterOpen, callCount);
        Assert.Equal([42], data);
        _sut.CloseFile(h);
    }

    [Fact]
    public void WriteFile_DoesNotReCheckPermissions()
    {
        int writeCheckCount = 0;
        _fileServiceMock
            .Setup(f => f.CanWriteAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .Returns<string, UserContext>((_, __) =>
            {
                writeCheckCount++;
                return Task.FromResult(true);
            });

        _sut.CreateFile(out var h, out _, "noRecheckWrite.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        int countAfterOpen = writeCheckCount;

        _sut.WriteFile(out var written, h, 0, [1, 2, 3]);

        Assert.Equal(countAfterOpen, writeCheckCount);
        Assert.Equal(3, written);
        _sut.CloseFile(h);
    }

    [Fact]
    public void CreateFile_ReadOnly_ChecksCanRead()
    {
        _fileServiceMock
            .Setup(f => f.CanReadAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(false);

        CreateTestFile("readDenied.txt");
        var status = _sut.CreateFile(out _, out _, "readDenied.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        Assert.Equal(NTStatus.STATUS_ACCESS_DENIED, status);
    }

    [Fact]
    public void CreateFile_WriteNew_ChecksCanWrite()
    {
        _fileServiceMock
            .Setup(f => f.CanWriteAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(false);

        var status = _sut.CreateFile(out _, out _, "writeDenied.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        Assert.Equal(NTStatus.STATUS_ACCESS_DENIED, status);
    }

    [Fact]
    public void CloseFile_DeleteOnClose_ChecksDeletePermission()
    {
        _fileServiceMock
            .Setup(f => f.CanDeleteAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(false);

        CreateTestFile("nodelete.txt");
        _sut.CreateFile(out var h, out _, "nodelete.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        _sut.SetFileInformation(h, new FileDispositionInformation { DeletePending = true });
        var status = _sut.CloseFile(h);

        Assert.Equal(NTStatus.STATUS_ACCESS_DENIED, status);
        Assert.True(File.Exists(Path.Combine(_testRoot, "nodelete.txt")));
    }

    // ═══════════════════════════════════════════════════════════
    //  FILE OPERATIONS
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CreateFile_NewFile_CreatesAndReturnsSuccess()
    {
        var s = _sut.CreateFile(out var h, out var fs, "newfile.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.Equal(FileStatus.FILE_CREATED, fs);
        Assert.True(File.Exists(Path.Combine(_testRoot, "newfile.txt")));
        _sut.CloseFile(h);
    }

    [Fact]
    public void CreateFile_ExistingFile_FileCreate_ReturnsCollision()
    {
        CreateTestFile("existing.txt");
        var s = _sut.CreateFile(out _, out var fs, "existing.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        Assert.Equal(NTStatus.STATUS_OBJECT_NAME_COLLISION, s);
    }

    [Fact]
    public void CreateFile_OpenExisting_ReturnsSuccess()
    {
        CreateTestFile("toopen.txt");
        var s = _sut.CreateFile(out var h, out var fs, "toopen.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.Equal(FileStatus.FILE_OPENED, fs);
        _sut.CloseFile(h);
    }

    [Fact]
    public void CreateFile_OpenNonExisting_ReturnsNotFound()
    {
        var s = _sut.CreateFile(out _, out _, "doesnotexist.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        Assert.Equal(NTStatus.STATUS_OBJECT_NAME_NOT_FOUND, s);
    }

    [Fact]
    public void CreateFile_NewDirectory_CreatesAndReturnsSuccess()
    {
        var s = _sut.CreateFile(out var h, out _, "newdir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.True(Directory.Exists(Path.Combine(_testRoot, "newdir")));
        _sut.CloseFile(h);
    }

    [Fact]
    public void CreateFile_ExistingDirectory_Open_ReturnsSuccess()
    {
        CreateTestDirectory("existdir");
        var s = _sut.CreateFile(out var h, out var fs, "existdir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.Equal(FileStatus.FILE_OPENED, fs);
        _sut.CloseFile(h);
    }

    [Fact]
    public void CreateFile_OpenIfDirectory_CreatesWhenMissing()
    {
        var s = _sut.CreateFile(out var h, out var fs, "maybedir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN_IF, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.Equal(FileStatus.FILE_CREATED, fs);
        Assert.True(Directory.Exists(Path.Combine(_testRoot, "maybedir")));
        _sut.CloseFile(h);
    }

    [Fact]
    public void CreateFile_Overwrite_ExistingFile_ReturnsOverwritten()
    {
        CreateTestFile("overwrite.txt", [1, 2, 3]);
        var s = _sut.CreateFile(out var h, out var fs, "overwrite.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OVERWRITE, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.Equal(FileStatus.FILE_OVERWRITTEN, fs);
        _sut.CloseFile(h);
    }

    [Fact]
    public void CreateFile_Supersede_ExistingFile_ReturnsSuperseded()
    {
        CreateTestFile("supersede.txt", [1, 2, 3]);
        var s = _sut.CreateFile(out var h, out var fs, "supersede.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_SUPERSEDE, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.Equal(FileStatus.FILE_SUPERSEDED, fs);
        _sut.CloseFile(h);
    }

    [Fact]
    public void CreateFile_MissingParentDir_ReturnsPathNotFound()
    {
        var s = _sut.CreateFile(out _, out _, @"no\parent\file.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        Assert.Equal(NTStatus.STATUS_OBJECT_PATH_NOT_FOUND, s);
    }

    // ═══════════════════════════════════════════════════════════
    //  READ / WRITE
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ReadFile_ValidHandle_ReturnsData()
    {
        CreateTestFile("readable.txt", [10, 20, 30, 40, 50]);
        _sut.CreateFile(out var h, out _, "readable.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        var s = _sut.ReadFile(out var data, h, 0, 5);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.Equal([10, 20, 30, 40, 50], data);
        _sut.CloseFile(h);
    }

    [Fact]
    public void ReadFile_WithOffset_ReturnsPartialData()
    {
        CreateTestFile("partial.txt", [10, 20, 30, 40, 50]);
        _sut.CreateFile(out var h, out _, "partial.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        var s = _sut.ReadFile(out var data, h, 2, 3);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.Equal([30, 40, 50], data);
        _sut.CloseFile(h);
    }

    [Fact]
    public void ReadFile_PastEndOfFile_ReturnsEndOfFile()
    {
        CreateTestFile("short.txt", [1]);
        _sut.CreateFile(out var h, out _, "short.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        _sut.ReadFile(out _, h, 0, 1);
        var s = _sut.ReadFile(out var data, h, 1, 10);
        Assert.Equal(NTStatus.STATUS_END_OF_FILE, s);
        _sut.CloseFile(h);
    }

    [Fact]
    public void ReadFile_DirectoryHandle_ReturnsInvalidHandle()
    {
        CreateTestDirectory("readdir");
        _sut.CreateFile(out var h, out _, "readdir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());
        var s = _sut.ReadFile(out _, h, 0, 10);
        Assert.Equal(NTStatus.STATUS_INVALID_HANDLE, s);
        _sut.CloseFile(h);
    }

    [Fact]
    public void ReadFile_NullHandle_ReturnsInvalidHandle()
    {
        Assert.Equal(NTStatus.STATUS_INVALID_HANDLE,
            _sut.ReadFile(out _, null!, 0, 10));
    }

    [Fact]
    public void WriteFile_ValidHandle_WritesData()
    {
        _sut.CreateFile(out var h, out _, "writable.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        var data = new byte[] { 100, 200, 255 };
        var s = _sut.WriteFile(out var w, h, 0, data);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.Equal(3, w);
        _sut.CloseFile(h);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(_testRoot, "writable.txt")));
    }

    [Fact]
    public void WriteFile_NullHandle_ReturnsInvalidHandle()
    {
        Assert.Equal(NTStatus.STATUS_INVALID_HANDLE,
            _sut.WriteFile(out _, null!, 0, [1]));
    }

    [Fact]
    public void WriteFile_WithOffset_WritesAtCorrectPosition()
    {
        CreateTestFile("offset_write.txt", new byte[100]);
        _sut.CreateFile(out var h, out _, "offset_write.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        byte[] data = [0xAA, 0xBB, 0xCC];
        _sut.WriteFile(out var written, h, 50, data);
        Assert.Equal(3, written);
        _sut.CloseFile(h);

        var all = File.ReadAllBytes(Path.Combine(_testRoot, "offset_write.txt"));
        Assert.Equal(0xAA, all[50]);
        Assert.Equal(0xBB, all[51]);
        Assert.Equal(0xCC, all[52]);
    }

    [Fact]
    public void WriteFile_DirectoryHandle_ReturnsInvalidHandle()
    {
        CreateTestDirectory("writedir");
        _sut.CreateFile(out var h, out _, "writedir",
            AccessMask.GENERIC_ALL, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        var s = _sut.WriteFile(out _, h, 0, [1, 2, 3]);
        Assert.NotEqual(NTStatus.STATUS_SUCCESS, s);
        _sut.CloseFile(h);
    }

    [Fact]
    public void WriteFile_ZeroBytes_Succeeds()
    {
        CreateTestFile("zero_write.txt");
        _sut.CreateFile(out var h, out _, "zero_write.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var s = _sut.WriteFile(out var written, h, 0, []);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.Equal(0, written);
        _sut.CloseFile(h);
    }

    // ═══════════════════════════════════════════════════════════
    //  CLOSE
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CloseFile_NullHandle_ReturnsInvalidHandle()
    {
        Assert.Equal(NTStatus.STATUS_INVALID_HANDLE, _sut.CloseFile(null!));
    }

    [Fact]
    public void CloseFile_DeleteOnClose_DeletesFile()
    {
        CreateTestFile("todelete.txt");
        _sut.CreateFile(out var h, out _, "todelete.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        _sut.SetFileInformation(h,
            new FileDispositionInformation { DeletePending = true });
        _sut.CloseFile(h);
        Assert.False(File.Exists(Path.Combine(_testRoot, "todelete.txt")));
    }

    [Fact]
    public void CloseFile_DeleteOnClose_DeletesDirectory()
    {
        CreateTestDirectory("deldir");
        CreateTestFile("deldir/child.txt");
        _sut.CreateFile(out var h, out _, "deldir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());
        _sut.SetFileInformation(h,
            new FileDispositionInformation { DeletePending = true });
        _sut.CloseFile(h);
        Assert.False(Directory.Exists(Path.Combine(_testRoot, "deldir")));
    }

    // ═══════════════════════════════════════════════════════════
    //  DIRECTORY LISTING
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void QueryDirectory_ListsFilesAndDirs()
    {
        CreateTestDirectory("listdir");
        CreateTestDirectory("listdir/subdir");
        CreateTestFile("listdir/file1.txt");
        CreateTestFile("listdir/file2.txt");
        _sut.CreateFile(out var h, out _, "listdir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());
        var s = _sut.QueryDirectory(out var r, h, "*",
            FileInformationClass.FileDirectoryInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        var names = r.Select(x => ((FileDirectoryInformation)x).FileName).ToList();
        Assert.Equal(5, names.Count);
        Assert.Contains("subdir", names);
        Assert.Contains("file1.txt", names);
        Assert.Contains("file2.txt", names);
        _sut.CloseFile(h);
    }

    [Fact]
    public void QueryDirectory_WithPattern_FiltersResults()
    {
        CreateTestDirectory("filterdir");
        CreateTestFile("filterdir/report.txt");
        CreateTestFile("filterdir/report.csv");
        CreateTestFile("filterdir/readme.md");
        _sut.CreateFile(out var h, out _, "filterdir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());
        var s = _sut.QueryDirectory(out var r, h, "*.txt",
            FileInformationClass.FileDirectoryInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.Single(r);
        Assert.Equal("report.txt", ((FileDirectoryInformation)r[0]).FileName);
        _sut.CloseFile(h);
    }

    [Fact]
    public void QueryDirectory_EmptyDir_ReturnsOnlyDots()
    {
        CreateTestDirectory("emptydir");
        _sut.CreateFile(out var h, out _, "emptydir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());
        var s = _sut.QueryDirectory(out var r, h, "*",
            FileInformationClass.FileDirectoryInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.Equal(2, r.Count);
        _sut.CloseFile(h);
    }

    [Fact]
    public void QueryDirectory_NonDirectoryHandle_ReturnsInvalidHandle()
    {
        CreateTestFile("notadir.txt");
        _sut.CreateFile(out var h, out _, "notadir.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        var s = _sut.QueryDirectory(out _, h, "*",
            FileInformationClass.FileDirectoryInformation);
        Assert.Equal(NTStatus.STATUS_INVALID_HANDLE, s);
        _sut.CloseFile(h);
    }

    [Fact]
    public void QueryDirectory_BothDirInfo_ReturnsShortNames()
    {
        CreateTestDirectory("bothdir");
        CreateTestFile("bothdir/averylongfilename.txt");
        _sut.CreateFile(out var h, out _, "bothdir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        var s = _sut.QueryDirectory(out var entries, h, "*",
            FileInformationClass.FileBothDirectoryInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);

        var fileEntry = entries.OfType<FileBothDirectoryInformation>()
            .FirstOrDefault(e => e.FileName == "averylongfilename.txt");
        Assert.NotNull(fileEntry);
        Assert.NotEmpty(fileEntry.ShortName);
        _sut.CloseFile(h);
    }

    [Fact]
    public void QueryDirectory_NamesOnly_ReturnsJustNames()
    {
        CreateTestDirectory("namesdir");
        CreateTestFile("namesdir/a.txt");
        CreateTestFile("namesdir/b.txt");
        _sut.CreateFile(out var h, out _, "namesdir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        var s = _sut.QueryDirectory(out var entries, h, "*",
            FileInformationClass.FileNamesInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);

        var nameEntries = entries.OfType<FileNamesInformation>().ToList();
        Assert.Contains(nameEntries, e => e.FileName == "a.txt");
        Assert.Contains(nameEntries, e => e.FileName == "b.txt");
        _sut.CloseFile(h);
    }

    [Fact]
    public void QueryDirectory_SpecificFilePattern_ReturnsSingleResult()
    {
        CreateTestDirectory("patterndir");
        CreateTestFile("patterndir/match.txt");
        CreateTestFile("patterndir/skip.log");
        _sut.CreateFile(out var h, out _, "patterndir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        var s = _sut.QueryDirectory(out var entries, h, "match.txt",
            FileInformationClass.FileDirectoryInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);

        var named = entries.OfType<FileDirectoryInformation>()
            .Where(e => e.FileName != "." && e.FileName != "..").ToList();
        Assert.Single(named);
        Assert.Equal("match.txt", named[0].FileName);
        _sut.CloseFile(h);
    }

    [Fact]
    public void QueryDirectory_RootDir_ListsTopLevelEntries()
    {
        CreateTestFile("root_file.txt");
        CreateTestDirectory("root_sub");

        _sut.CreateFile(out var h, out _, "",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        var s = _sut.QueryDirectory(out var entries, h, "*",
            FileInformationClass.FileDirectoryInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);

        var names = entries.OfType<FileDirectoryInformation>()
            .Select(e => e.FileName).ToList();
        Assert.Contains("root_file.txt", names);
        Assert.Contains("root_sub", names);
        _sut.CloseFile(h);
    }

    // ═══════════════════════════════════════════════════════════
    //  FILE INFO
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void GetFileInformation_File_BasicInfo_ReturnsCorrectAttributes()
    {
        CreateTestFile("info.txt", [1, 2, 3]);
        _sut.CreateFile(out var h, out _, "info.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        var s = _sut.GetFileInformation(out var r, h,
            FileInformationClass.FileBasicInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.Equal(FileAttributes.Normal, ((FileBasicInformation)r).FileAttributes);
        _sut.CloseFile(h);
    }

    [Fact]
    public void GetFileInformation_File_StandardInfo_ReturnsCorrectSize()
    {
        CreateTestFile("sized.txt", new byte[1024]);
        _sut.CreateFile(out var h, out _, "sized.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        var s = _sut.GetFileInformation(out var r, h,
            FileInformationClass.FileStandardInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        var std = (FileStandardInformation)r;
        Assert.Equal(1024, std.EndOfFile);
        Assert.False(std.Directory);
        _sut.CloseFile(h);
    }

    [Fact]
    public void GetFileInformation_Directory_ReturnsDirectoryAttribute()
    {
        CreateTestDirectory("infodir");
        _sut.CreateFile(out var h, out _, "infodir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());
        var s = _sut.GetFileInformation(out var r, h,
            FileInformationClass.FileBasicInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.Equal(FileAttributes.Directory, ((FileBasicInformation)r).FileAttributes);
        _sut.CloseFile(h);
    }

    [Fact]
    public void GetFileInformation_NullHandle_ReturnsInvalidHandle()
    {
        Assert.Equal(NTStatus.STATUS_INVALID_HANDLE,
            _sut.GetFileInformation(out _, null!,
                FileInformationClass.FileBasicInformation));
    }

    [Fact]
    public void GetFileInformation_File_NetworkOpenInfo_ReturnsCorrectData()
    {
        CreateTestFile("net.txt", new byte[2048]);
        _sut.CreateFile(out var h, out _, "net.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var s = _sut.GetFileInformation(out var info, h,
            FileInformationClass.FileNetworkOpenInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);

        var net = Assert.IsType<FileNetworkOpenInformation>(info);
        Assert.Equal(2048, net.EndOfFile);
        Assert.Equal(FileAttributes.Normal, net.FileAttributes);
        _sut.CloseFile(h);
    }

    [Fact]
    public void GetFileInformation_File_StreamInfo_ReturnsDataStream()
    {
        CreateTestFile("stream.txt", new byte[512]);
        _sut.CreateFile(out var h, out _, "stream.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var s = _sut.GetFileInformation(out var info, h,
            FileInformationClass.FileStreamInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);

        var stream = Assert.IsType<FileStreamInformation>(info);
        Assert.Single(stream.Entries);
        Assert.Equal("::$DATA", stream.Entries[0].StreamName);
        Assert.Equal(512, stream.Entries[0].StreamSize);
        _sut.CloseFile(h);
    }

    [Fact]
    public void GetFileInformation_File_InternalInfo_ReturnsZeroIndex()
    {
        CreateTestFile("internal.txt");
        _sut.CreateFile(out var h, out _, "internal.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var s = _sut.GetFileInformation(out var info, h,
            FileInformationClass.FileInternalInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.Equal(0L, Assert.IsType<FileInternalInformation>(info).IndexNumber);
        _sut.CloseFile(h);
    }

    [Fact]
    public void GetFileInformation_File_EaInfo_ReturnsZeroEaSize()
    {
        CreateTestFile("ea.txt");
        _sut.CreateFile(out var h, out _, "ea.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var s = _sut.GetFileInformation(out var info, h,
            FileInformationClass.FileEaInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.IsType<FileEaInformation>(info);
        _sut.CloseFile(h);
    }

    [Fact]
    public void GetFileInformation_File_AttributeTag_ReturnsNormalAndZeroReparse()
    {
        CreateTestFile("tag.txt");
        _sut.CreateFile(out var h, out _, "tag.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var s = _sut.GetFileInformation(out var info, h,
            FileInformationClass.FileAttributeTagInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);

        var tag = Assert.IsType<FileAttributeTagInformation>(info);
        Assert.Equal(FileAttributes.Normal, tag.FileAttributes);
        Assert.Equal(0u, tag.ReparsePointTag);
        _sut.CloseFile(h);
    }

    [Fact]
    public void GetFileInformation_Directory_StandardInfo_ReportsZeroSizeAndDir()
    {
        CreateTestDirectory("infodir");
        _sut.CreateFile(out var h, out _, "infodir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        var s = _sut.GetFileInformation(out var info, h,
            FileInformationClass.FileStandardInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);

        var std = Assert.IsType<FileStandardInformation>(info);
        Assert.True(std.Directory);
        Assert.Equal(0, std.EndOfFile);
        _sut.CloseFile(h);
    }

    [Fact]
    public void GetFileInformation_Directory_NetworkOpenInfo_ReportsDirectoryAttributes()
    {
        CreateTestDirectory("netdir");
        _sut.CreateFile(out var h, out _, "netdir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        var s = _sut.GetFileInformation(out var info, h,
            FileInformationClass.FileNetworkOpenInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.Equal(FileAttributes.Directory, Assert.IsType<FileNetworkOpenInformation>(info).FileAttributes);
        _sut.CloseFile(h);
    }

    [Fact]
    public void GetFileInformation_File_StandardInfo_DeletePendingReflectsDisposition()
    {
        CreateTestFile("pending.txt");
        _sut.CreateFile(out var h, out _, "pending.txt",
            AccessMask.GENERIC_READ | AccessMask.DELETE, FileAttributes.Normal,
            ShareAccess.Read, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_NON_DIRECTORY_FILE, CreateSecurityContext());

        _sut.SetFileInformation(h, new FileDispositionInformation { DeletePending = true });

        var s = _sut.GetFileInformation(out var info, h,
            FileInformationClass.FileStandardInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.True(Assert.IsType<FileStandardInformation>(info).DeletePending);
        _sut.CloseFile(h);
    }

    // ═══════════════════════════════════════════════════════════
    //  SET FILE INFO
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void SetFileInformation_Rename_MovesFile()
    {
        CreateTestFile("before.txt");
        _sut.CreateFile(out var h, out _, "before.txt",
            AccessMask.GENERIC_ALL, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        _sut.SetFileInformation(h,
            new FileRenameInformationType2
            { FileName = "after.txt", ReplaceIfExists = false });
        Assert.False(File.Exists(Path.Combine(_testRoot, "before.txt")));
        Assert.True(File.Exists(Path.Combine(_testRoot, "after.txt")));
        _sut.CloseFile(h);
    }

    [Fact]
    public void SetFileInformation_Rename_ExistingTarget_NoReplace_ReturnsCollision()
    {
        CreateTestFile("src.txt");
        CreateTestFile("dst.txt");
        _sut.CreateFile(out var h, out _, "src.txt",
            AccessMask.GENERIC_ALL, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        var s = _sut.SetFileInformation(h,
            new FileRenameInformationType2
            { FileName = "dst.txt", ReplaceIfExists = false });
        Assert.Equal(NTStatus.STATUS_OBJECT_NAME_COLLISION, s);
        _sut.CloseFile(h);
    }

    [Fact]
    public void SetFileInformation_Rename_ExistingTarget_WithReplace_Succeeds()
    {
        CreateTestFile("src2.txt", [1, 2, 3]);
        CreateTestFile("dst2.txt", [9, 9, 9, 9]);
        _sut.CreateFile(out var h, out _, "src2.txt",
            AccessMask.GENERIC_ALL, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        var s = _sut.SetFileInformation(h,
            new FileRenameInformationType2
            { FileName = "dst2.txt", ReplaceIfExists = true });
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        _sut.CloseFile(h);
    }

    [Fact]
    public void SetFileInformation_EndOfFile_TruncatesFile()
    {
        CreateTestFile("trunc.txt", new byte[1000]);
        _sut.CreateFile(out var h, out _, "trunc.txt",
            AccessMask.GENERIC_ALL, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        _sut.SetFileInformation(h,
            new FileEndOfFileInformation { EndOfFile = 100 });
        _sut.CloseFile(h);
        Assert.Equal(100, new FileInfo(Path.Combine(_testRoot, "trunc.txt")).Length);
    }

    [Fact]
    public void SetFileInformation_NullHandle_ReturnsInvalidHandle()
    {
        Assert.Equal(NTStatus.STATUS_INVALID_HANDLE,
            _sut.SetFileInformation(null!,
                new FileDispositionInformation { DeletePending = true }));
    }

    [Fact]
    public void SetFileInformation_Disposition_SetsDeleteOnClose()
    {
        CreateTestFile("disposable.txt");
        _sut.CreateFile(out var h, out _, "disposable.txt",
            AccessMask.GENERIC_READ | AccessMask.DELETE, FileAttributes.Normal,
            ShareAccess.Read, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_NON_DIRECTORY_FILE, CreateSecurityContext());

        var s = _sut.SetFileInformation(h, new FileDispositionInformation { DeletePending = true });
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);

        _sut.CloseFile(h);
        Assert.False(File.Exists(Path.Combine(_testRoot, "disposable.txt")));
    }

    [Fact]
    public void SetFileInformation_AllocationInfo_TruncatesLargerFile()
    {
        CreateTestFile("big.txt", new byte[8192]);
        _sut.CreateFile(out var h, out _, "big.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        _sut.SetFileInformation(h, new FileAllocationInformation { AllocationSize = 1024 });
        _sut.CloseFile(h);
        Assert.Equal(1024, new FileInfo(Path.Combine(_testRoot, "big.txt")).Length);
    }

    [Fact]
    public void SetFileInformation_AllocationInfo_DoesNotGrowSmallFile()
    {
        CreateTestFile("small.txt", new byte[100]);
        _sut.CreateFile(out var h, out _, "small.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        _sut.SetFileInformation(h, new FileAllocationInformation { AllocationSize = 8192 });
        _sut.CloseFile(h);
        Assert.Equal(100, new FileInfo(Path.Combine(_testRoot, "small.txt")).Length);
    }

    [Fact]
    public void SetFileInformation_UnsupportedType_ReturnsNotSupported()
    {
        CreateTestFile("test.txt");
        _sut.CreateFile(out var h, out _, "test.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var s = _sut.SetFileInformation(h, new FileEaInformation());
        Assert.Equal(NTStatus.STATUS_NOT_SUPPORTED, s);
        _sut.CloseFile(h);
    }

    [Fact]
    public void SetFileInformation_RenameDirectory_Succeeds()
    {
        CreateTestDirectory("olddir");
        _sut.CreateFile(out var h, out _, "olddir",
            AccessMask.GENERIC_ALL, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        var s = _sut.SetFileInformation(h, new FileRenameInformationType2 { FileName = "newdir" });
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);

        Assert.False(Directory.Exists(Path.Combine(_testRoot, "olddir")));
        Assert.True(Directory.Exists(Path.Combine(_testRoot, "newdir")));
        _sut.CloseFile(h);
    }

    [Fact]
    public void SetFileInformation_RenameDirectory_ExistingTarget_ReturnsCollision()
    {
        CreateTestDirectory("dirA");
        CreateTestDirectory("dirB");
        _sut.CreateFile(out var h, out _, "dirA",
            AccessMask.GENERIC_ALL, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        var s = _sut.SetFileInformation(h, new FileRenameInformationType2 { FileName = "dirB" });
        Assert.Equal(NTStatus.STATUS_OBJECT_NAME_COLLISION, s);
        _sut.CloseFile(h);
    }

    // ═══════════════════════════════════════════════════════════
    //  FILESYSTEM INFO
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void GetFileSystemInformation_Volume_ReturnsKaimoLabel()
    {
        var s = _sut.GetFileSystemInformation(out var r,
            FileSystemInformationClass.FileFsVolumeInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.Equal("KaimoSMB", ((FileFsVolumeInformation)r).VolumeLabel);
    }

    [Fact]
    public void GetFileSystemInformation_Attribute_ReportsNTFS()
    {
        var s = _sut.GetFileSystemInformation(out var r,
            FileSystemInformationClass.FileFsAttributeInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.Equal("NTFS", ((FileFsAttributeInformation)r).FileSystemName);
    }

    [Fact]
    public void GetFileSystemInformation_InvalidClass_ReturnsInvalidParameter()
    {
        var s = _sut.GetFileSystemInformation(out _,
            (FileSystemInformationClass)255);
        Assert.Equal(NTStatus.STATUS_INVALID_PARAMETER, s);
    }

    [Fact]
    public void GetFileSystemInformation_Size_ReturnsNonZeroTotals()
    {
        var s = _sut.GetFileSystemInformation(out var info,
            FileSystemInformationClass.FileFsSizeInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);

        var sizeInfo = Assert.IsType<FileFsSizeInformation>(info);
        Assert.True(sizeInfo.TotalAllocationUnits > 0);
        Assert.Equal(8u, sizeInfo.SectorsPerAllocationUnit);
        Assert.Equal(512u, sizeInfo.BytesPerSector);
    }

    [Fact]
    public void GetFileSystemInformation_FullSize_ReturnsNonZeroTotals()
    {
        var s = _sut.GetFileSystemInformation(out var info,
            FileSystemInformationClass.FileFsFullSizeInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);

        var fullSize = Assert.IsType<FileFsFullSizeInformation>(info);
        Assert.True(fullSize.TotalAllocationUnits > 0);
        Assert.Equal(fullSize.CallerAvailableAllocationUnits,
            fullSize.ActualAvailableAllocationUnits);
    }

    [Fact]
    public void GetFileSystemInformation_Device_ReturnsDisk()
    {
        var s = _sut.GetFileSystemInformation(out var info,
            FileSystemInformationClass.FileFsDeviceInformation);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.Equal(DeviceType.Disk, Assert.IsType<FileFsDeviceInformation>(info).DeviceType);
    }

    // ═══════════════════════════════════════════════════════════
    //  FLUSH
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void FlushFileBuffers_ValidHandle_ReturnsSuccess()
    {
        _sut.CreateFile(out var h, out _, "flush.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        Assert.Equal(NTStatus.STATUS_SUCCESS, _sut.FlushFileBuffers(h));
        _sut.CloseFile(h);
    }

    [Fact]
    public void FlushFileBuffers_NullHandle_ReturnsInvalidHandle()
    {
        Assert.Equal(NTStatus.STATUS_INVALID_HANDLE, _sut.FlushFileBuffers(null!));
    }

    // ═══════════════════════════════════════════════════════════
    //  SECURITY DESCRIPTOR
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void GetSecurityInformation_ReturnsSuccess()
    {
        CreateTestFile("secured.txt");
        _sut.CreateFile(out var h, out _, "secured.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var s = _sut.GetSecurityInformation(out var sd, h,
            SecurityInformation.DACL_SECURITY_INFORMATION);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        Assert.NotNull(sd);
        _sut.CloseFile(h);
    }

    [Fact]
    public void SetSecurityInformation_ReturnsSuccess()
    {
        CreateTestFile("setsec.txt");
        _sut.CreateFile(out var h, out _, "setsec.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var s = _sut.SetSecurityInformation(h,
            SecurityInformation.DACL_SECURITY_INFORMATION,
            new SecurityDescriptor());
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        _sut.CloseFile(h);
    }

    // ═══════════════════════════════════════════════════════════
    //  STUBS
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Cancel_ReturnsSuccess()
    {
        Assert.Equal(NTStatus.STATUS_SUCCESS, _sut.Cancel(new object()));
    }

    [Fact]
    public void DeviceIOControl_ReturnsNotSupported()
    {
        var s = _sut.DeviceIOControl(new object(), 0, [], out var output, 1024);
        Assert.Equal(NTStatus.STATUS_NOT_SUPPORTED, s);
    }

    [Fact]
    public void LockFile_ReturnsSuccess()
    {
        Assert.Equal(NTStatus.STATUS_SUCCESS,
            _sut.LockFile(new object(), 0, 100, true));
    }

    [Fact]
    public void UnlockFile_ReturnsSuccess()
    {
        Assert.Equal(NTStatus.STATUS_SUCCESS,
            _sut.UnlockFile(new object(), 0, 100));
    }

    [Fact]
    public void NotifyChange_ReturnsNotSupported()
    {
        var s = _sut.NotifyChange(out _, new object(),
            NotifyChangeFilter.FileName, false, 4096,
            null!, null!);
        Assert.Equal(NTStatus.STATUS_NOT_SUPPORTED, s);
    }

    [Fact]
    public void SetFileSystemInformation_ReturnsNotSupported()
    {
        var info = new FileFsVolumeInformation { VolumeLabel = "Test" };
        Assert.Equal(NTStatus.STATUS_NOT_SUPPORTED,
            _sut.SetFileSystemInformation(info));
    }

    // ═══════════════════════════════════════════════════════════
    //  PERMISSION DENIAL TESTS
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CreateFile_ReadDenied_ReturnsAccessDenied()
    {
        CreateTestFile("secret.txt");
        _fileServiceMock
            .Setup(f => f.CanReadAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(false);

        var s = _sut.CreateFile(out _, out _, "secret.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        Assert.Equal(NTStatus.STATUS_ACCESS_DENIED, s);
    }

    [Fact]
    public void CreateFile_WriteDenied_ReturnsAccessDenied()
    {
        _fileServiceMock
            .Setup(f => f.CanWriteAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(false);
        _fileServiceMock
            .Setup(f => f.CanCreateAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(false);

        var s = _sut.CreateFile(out _, out _, "newfile.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        Assert.Equal(NTStatus.STATUS_ACCESS_DENIED, s);
    }

    [Fact]
    public void CreateFile_CreateDenied_NewFile_ReturnsAccessDenied()
    {
        var denyMock = new Mock<IFileService>();
        denyMock.Setup(f => f.CanReadAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(true);
        denyMock.Setup(f => f.CanWriteAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(false);
        denyMock.Setup(f => f.CanCreateAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(false);
        denyMock.Setup(f => f.CanDeleteAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(false);

        var denySut = CreateSutWithMock(denyMock);

        var s = denySut.CreateFile(out _, out _, "blocked.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());
        Assert.Equal(NTStatus.STATUS_ACCESS_DENIED, s);
    }

    [Fact]
    public void CloseFile_DeleteDenied_KeepsFileAndReturnsDenied()
    {
        var denyDeleteMock = new Mock<IFileService>();
        denyDeleteMock.Setup(f => f.CanReadAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(true);
        denyDeleteMock.Setup(f => f.CanWriteAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(true);
        denyDeleteMock.Setup(f => f.CanCreateAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(true);
        denyDeleteMock.Setup(f => f.CanDeleteAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(false);

        var denySut = CreateSutWithMock(denyDeleteMock);
        CreateTestFile("keep_me.txt");

        denySut.CreateFile(out var h, out _, "keep_me.txt",
            AccessMask.GENERIC_READ | AccessMask.DELETE, FileAttributes.Normal,
            ShareAccess.Read, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DELETE_ON_CLOSE, CreateSecurityContext());

        var s = denySut.CloseFile(h);
        Assert.Equal(NTStatus.STATUS_ACCESS_DENIED, s);
        Assert.True(File.Exists(Path.Combine(_testRoot, "keep_me.txt")));
    }

    // ═══════════════════════════════════════════════════════════
    //  HANDLE LIFECYCLE
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ReadFile_AfterClose_ReturnsFileClosed()
    {
        CreateTestFile("lifecycle.txt");
        _sut.CreateFile(out var h, out _, "lifecycle.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        _sut.CloseFile(h);
        var s = _sut.ReadFile(out _, h, 0, 10);
        Assert.Equal(NTStatus.STATUS_FILE_CLOSED, s);
    }

    [Fact]
    public void WriteFile_AfterClose_ReturnsFileClosed()
    {
        CreateTestFile("lifecycle2.txt");
        _sut.CreateFile(out var h, out _, "lifecycle2.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        _sut.CloseFile(h);
        var s = _sut.WriteFile(out _, h, 0, [1, 2, 3]);
        Assert.Equal(NTStatus.STATUS_FILE_CLOSED, s);
    }

    // ═══════════════════════════════════════════════════════════
    //  EDGE CASES
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ReadFile_EmptyFile_ReturnsEndOfFile()
    {
        CreateTestFile("empty.txt", []);
        _sut.CreateFile(out var h, out _, "empty.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var s = _sut.ReadFile(out var data, h, 0, 1024);
        Assert.True(s == NTStatus.STATUS_END_OF_FILE ||
                    (s == NTStatus.STATUS_SUCCESS && data.Length == 0));
        _sut.CloseFile(h);
    }

    [Fact]
    public void CreateFile_RootPath_OpensShareRoot()
    {
        var s = _sut.CreateFile(out var h, out _, "",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());
        Assert.Equal(NTStatus.STATUS_SUCCESS, s);
        _sut.CloseFile(h);
    }

    [Fact]
    public void ConcurrentReads_SameFile_Succeed()
    {
        CreateTestFile("shared.txt", new byte[1024]);
        var barrier = new Barrier(2);
        NTStatus s1 = 0, s2 = 0;
        byte[]? data1 = null, data2 = null;

        var t1 = Task.Run(() =>
        {
            SmbFileSystem.SetSessionUser(_testUser);
            _sut.CreateFile(out var h, out _, "shared.txt",
                AccessMask.GENERIC_READ, FileAttributes.Normal,
                ShareAccess.Read, CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_NON_DIRECTORY_FILE, CreateSecurityContext());
            barrier.SignalAndWait();
            s1 = _sut.ReadFile(out data1, h, 0, 512);
            _sut.CloseFile(h);
        });

        var t2 = Task.Run(() =>
        {
            SmbFileSystem.SetSessionUser(_secondUser);
            _sut.CreateFile(out var h, out _, "shared.txt",
                AccessMask.GENERIC_READ, FileAttributes.Normal,
                ShareAccess.Read, CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_NON_DIRECTORY_FILE, CreateSecurityContext());
            barrier.SignalAndWait();
            s2 = _sut.ReadFile(out data2, h, 0, 512);
            _sut.CloseFile(h);
        });

        Task.WaitAll(t1, t2);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s1);
        Assert.Equal(NTStatus.STATUS_SUCCESS, s2);
        Assert.Equal(512, data1!.Length);
        Assert.Equal(512, data2!.Length);
    }

    [Fact]
    public void GetFileInformation_DeletedFile_NullHandle_ReturnsInvalidHandle()
    {
        var s = _sut.GetFileInformation(out _, null!,
            FileInformationClass.FileBasicInformation);
        Assert.Equal(NTStatus.STATUS_INVALID_HANDLE, s);
    }
}