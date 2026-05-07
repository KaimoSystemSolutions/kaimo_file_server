using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
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
    private readonly Mock<IFileService> _fileServiceMock;
    private readonly SmbFileSystem _sut;
    private readonly UserContext _testUser;

    public SmbFileSystemTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "kaimo_smb_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _fileServiceMock = new Mock<IFileService>();
        _fileServiceMock.Setup(f => f.CanReadAsync(It.IsAny<string>(), It.IsAny<UserContext>())).ReturnsAsync(true);
        _fileServiceMock.Setup(f => f.CanWriteAsync(It.IsAny<string>(), It.IsAny<UserContext>())).ReturnsAsync(true);
        _fileServiceMock.Setup(f => f.CanCreateAsync(It.IsAny<string>(), It.IsAny<UserContext>())).ReturnsAsync(true);
        _fileServiceMock.Setup(f => f.CanDeleteAsync(It.IsAny<string>(), It.IsAny<UserContext>())).ReturnsAsync(true);
        _sut = new SmbFileSystem(_testRoot, _fileServiceMock.Object);
        var user = new User(Guid.NewGuid(), "Test User", "testuser", "hash", "nthash");
        _testUser = new UserContext(user, [], [], []);
        _sut.CurrentUser = _testUser;
    }

    public void Dispose() { try { if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, true); } catch { } }

    private SecurityContext CreateSecurityContext() => null!;
    private void CreateTestFile(string rel, byte[]? content = null) { var p = Path.Combine(_testRoot, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllBytes(p, content ?? [1, 2, 3, 4, 5]); }
    private void CreateTestDirectory(string rel) { Directory.CreateDirectory(Path.Combine(_testRoot, rel)); }

    [Fact]
    public void CreateFile_NewFile_CreatesAndReturnsSuccess()
    { var s = _sut.CreateFile(out var h, out var fs, "newfile.txt", AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE, CreateSecurityContext()); Assert.Equal(NTStatus.STATUS_SUCCESS, s); Assert.Equal(FileStatus.FILE_CREATED, fs); Assert.True(File.Exists(Path.Combine(_testRoot, "newfile.txt"))); _sut.CloseFile(h); }

    [Fact]
    public void CreateFile_ExistingFile_FileCreate_ReturnsCollision()
    { CreateTestFile("existing.txt"); var s = _sut.CreateFile(out var h, out var fs, "existing.txt", AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE, CreateSecurityContext()); Assert.Equal(NTStatus.STATUS_OBJECT_NAME_COLLISION, s); }

    [Fact]
    public void CreateFile_OpenExisting_ReturnsSuccess()
    { CreateTestFile("toopen.txt"); var s = _sut.CreateFile(out var h, out var fs, "toopen.txt", AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, CreateSecurityContext()); Assert.Equal(NTStatus.STATUS_SUCCESS, s); Assert.Equal(FileStatus.FILE_OPENED, fs); _sut.CloseFile(h); }

    [Fact]
    public void CreateFile_OpenNonExisting_ReturnsNotFound()
    { var s = _sut.CreateFile(out var h, out var fs, "doesnotexist.txt", AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, CreateSecurityContext()); Assert.Equal(NTStatus.STATUS_OBJECT_NAME_NOT_FOUND, s); }

    [Fact]
    public void CreateFile_NewDirectory_CreatesAndReturnsSuccess()
    { var s = _sut.CreateFile(out var h, out var fs, "newdir", AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read, CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE, CreateSecurityContext()); Assert.Equal(NTStatus.STATUS_SUCCESS, s); Assert.True(Directory.Exists(Path.Combine(_testRoot, "newdir"))); _sut.CloseFile(h); }

    [Fact]
    public void CreateFile_NoUser_ReturnsError()
    { _sut.CurrentUser = null; 
        Assert.Throws<InvalidOperationException>(() => _sut.CreateFile(out var h, out var fs, "noaccess.txt", AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE, CreateSecurityContext())); }

    [Fact]
    public void CreateFile_WriteAccessDenied_ReturnsAccessDenied()
    { _fileServiceMock.Setup(f => f.CanWriteAsync(It.IsAny<string>(), It.IsAny<UserContext>())).ReturnsAsync(false); _fileServiceMock.Setup(f => f.CanCreateAsync(It.IsAny<string>(), It.IsAny<UserContext>())).ReturnsAsync(false); var s = _sut.CreateFile(out var h, out var fs, "denied.txt", AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE, CreateSecurityContext()); Assert.Equal(NTStatus.STATUS_ACCESS_DENIED, s); }

    [Fact]
    public void ReadFile_ValidHandle_ReturnsData()
    { CreateTestFile("readable.txt", [10, 20, 30, 40, 50]); _sut.CreateFile(out var h, out _, "readable.txt", AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, CreateSecurityContext()); var s = _sut.ReadFile(out var data, h, 0, 5); Assert.Equal(NTStatus.STATUS_SUCCESS, s); Assert.Equal([10, 20, 30, 40, 50], data); _sut.CloseFile(h); }

    [Fact]
    public void WriteFile_ValidHandle_WritesData()
    { _sut.CreateFile(out var h, out _, "writable.txt", AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE, CreateSecurityContext()); var data = new byte[] { 100, 200, 255 }; var s = _sut.WriteFile(out var w, h, 0, data); Assert.Equal(NTStatus.STATUS_SUCCESS, s); Assert.Equal(3, w); _sut.CloseFile(h); Assert.Equal(data, File.ReadAllBytes(Path.Combine(_testRoot, "writable.txt"))); }

    [Fact] public void CloseFile_NullHandle_ReturnsInvalidHandle() { Assert.Equal(NTStatus.STATUS_INVALID_HANDLE, _sut.CloseFile(null!)); }

    [Fact]
    public void CloseFile_DeleteOnClose_DeletesFile()
    { CreateTestFile("todelete.txt"); _sut.CreateFile(out var h, out _, "todelete.txt", AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, CreateSecurityContext()); _sut.SetFileInformation(h, new FileDispositionInformation { DeletePending = true }); _sut.CloseFile(h); Assert.False(File.Exists(Path.Combine(_testRoot, "todelete.txt"))); }

    [Fact]
    public void QueryDirectory_ListsFilesAndDirs()
    { CreateTestDirectory("listdir"); CreateTestDirectory("listdir/subdir"); CreateTestFile("listdir/file1.txt"); CreateTestFile("listdir/file2.txt"); _sut.CreateFile(out var h, out _, "listdir", AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, CreateSecurityContext()); var s = _sut.QueryDirectory(out var r, h, "*", FileInformationClass.FileDirectoryInformation); Assert.Equal(NTStatus.STATUS_SUCCESS, s); var names = r.Select(x => ((FileDirectoryInformation)x).FileName).ToList(); Assert.Equal(5, names.Count); Assert.Contains("subdir", names); _sut.CloseFile(h); }

    [Fact]
    public void GetFileInformation_File_BasicInfo_ReturnsCorrectAttributes()
    { CreateTestFile("info.txt", [1, 2, 3]); _sut.CreateFile(out var h, out _, "info.txt", AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, CreateSecurityContext()); var s = _sut.GetFileInformation(out var r, h, FileInformationClass.FileBasicInformation); Assert.Equal(NTStatus.STATUS_SUCCESS, s); Assert.Equal(FileAttributes.Normal, ((FileBasicInformation)r).FileAttributes); _sut.CloseFile(h); }

    [Fact]
    public void GetFileSystemInformation_Volume_ReturnsKaimoLabel()
    { var s = _sut.GetFileSystemInformation(out var r, FileSystemInformationClass.FileFsVolumeInformation); Assert.Equal(NTStatus.STATUS_SUCCESS, s); Assert.Equal("KaimoSMB", ((FileFsVolumeInformation)r).VolumeLabel); }

    [Fact]
    public void SetFileInformation_Rename_MovesFile()
    { CreateTestFile("before.txt"); _sut.CreateFile(out var h, out _, "before.txt", AccessMask.GENERIC_ALL, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, CreateSecurityContext()); _sut.SetFileInformation(h, new FileRenameInformationType2 { FileName = "after.txt", ReplaceIfExists = false }); Assert.False(File.Exists(Path.Combine(_testRoot, "before.txt"))); Assert.True(File.Exists(Path.Combine(_testRoot, "after.txt"))); _sut.CloseFile(h); }
}
