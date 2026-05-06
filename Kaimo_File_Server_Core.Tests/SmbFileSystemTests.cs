using Kaimo_File_Server_Core.Core.Domain;
using Kaimo_File_Server_Core.Core.Domain.Identity;
using Kaimo_File_Server_Core.Core.Services;
using Kaimo_File_Server_Core.Smb;
using Moq;
using SMBLibrary;
using SMBLibrary.Server;
using Xunit;
using FileAttributes = SMBLibrary.FileAttributes;

namespace Kaimo_File_Server_Core.Tests;

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

        // Default: alle Berechtigungen erlauben
        _fileServiceMock.Setup(f => f.CanReadAsync(It.IsAny<string>(), It.IsAny<UserContext>())).ReturnsAsync(true);
        _fileServiceMock.Setup(f => f.CanWriteAsync(It.IsAny<string>(), It.IsAny<UserContext>())).ReturnsAsync(true);
        _fileServiceMock.Setup(f => f.CanCreateAsync(It.IsAny<string>(), It.IsAny<UserContext>())).ReturnsAsync(true);
        _fileServiceMock.Setup(f => f.CanDeleteAsync(It.IsAny<string>(), It.IsAny<UserContext>())).ReturnsAsync(true);

        _sut = new SmbFileSystem(_testRoot, _fileServiceMock.Object);

        var user = new User(Guid.NewGuid(), "Test User", "testuser", "hash", "nthash");
        _testUser = new UserContext(user, [], [], []);
        _sut.CurrentUser = _testUser;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, true);
        }
        catch { /* Cleanup best-effort */ }
    }

    // ========== Helper ==========

    private SecurityContext CreateSecurityContext()
    {
        // SMBLibrary SecurityContext brauchen wir nicht wirklich, aber die Signatur verlangt es
        return null!;
    }

    private void CreateTestFile(string relativePath, byte[]? content = null)
    {
        var fullPath = Path.Combine(_testRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content ?? [1, 2, 3, 4, 5]);
    }

    private void CreateTestDirectory(string relativePath)
    {
        Directory.CreateDirectory(Path.Combine(_testRoot, relativePath));
    }

    // ===================== CreateFile — Dateien =====================

    [Fact]
    public void CreateFile_NewFile_CreatesAndReturnsSuccess()
    {
        var status = _sut.CreateFile(
            out var handle, out var fileStatus,
            "newfile.txt",
            AccessMask.GENERIC_WRITE,
            FileAttributes.Normal,
            ShareAccess.Read,
            CreateDisposition.FILE_CREATE,
            CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        Assert.Equal(FileStatus.FILE_CREATED, fileStatus);
        Assert.NotNull(handle);
        Assert.True(File.Exists(Path.Combine(_testRoot, "newfile.txt")));

        _sut.CloseFile(handle);
    }

    [Fact]
    public void CreateFile_ExistingFile_FileCreate_ReturnsCollision()
    {
        CreateTestFile("existing.txt");

        var status = _sut.CreateFile(
            out var handle, out var fileStatus,
            "existing.txt",
            AccessMask.GENERIC_WRITE,
            FileAttributes.Normal,
            ShareAccess.Read,
            CreateDisposition.FILE_CREATE,
            CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        Assert.Equal(NTStatus.STATUS_OBJECT_NAME_COLLISION, status);
        Assert.Equal(FileStatus.FILE_EXISTS, fileStatus);
    }

    [Fact]
    public void CreateFile_OpenExisting_ReturnsSuccess()
    {
        CreateTestFile("toopen.txt");

        var status = _sut.CreateFile(
            out var handle, out var fileStatus,
            "toopen.txt",
            AccessMask.GENERIC_READ,
            FileAttributes.Normal,
            ShareAccess.Read,
            CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        Assert.Equal(FileStatus.FILE_OPENED, fileStatus);

        _sut.CloseFile(handle);
    }

    [Fact]
    public void CreateFile_OpenNonExisting_ReturnsNotFound()
    {
        var status = _sut.CreateFile(
            out var handle, out var fileStatus,
            "doesnotexist.txt",
            AccessMask.GENERIC_READ,
            FileAttributes.Normal,
            ShareAccess.Read,
            CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        Assert.Equal(NTStatus.STATUS_OBJECT_NAME_NOT_FOUND, status);
    }

    [Fact]
    public void CreateFile_OpenOrCreate_NonExisting_Creates()
    {
        var status = _sut.CreateFile(
            out var handle, out var fileStatus,
            "openorcreate.txt",
            AccessMask.GENERIC_WRITE,
            FileAttributes.Normal,
            ShareAccess.Read,
            CreateDisposition.FILE_OPEN_IF,
            CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        Assert.Equal(FileStatus.FILE_CREATED, fileStatus);
        Assert.True(File.Exists(Path.Combine(_testRoot, "openorcreate.txt")));

        _sut.CloseFile(handle);
    }

    [Fact]
    public void CreateFile_Overwrite_ExistingFile_Truncates()
    {
        CreateTestFile("tooverwrite.txt", new byte[] { 1, 2, 3, 4, 5 });

        var status = _sut.CreateFile(
            out var handle, out var fileStatus,
            "tooverwrite.txt",
            AccessMask.GENERIC_WRITE,
            FileAttributes.Normal,
            ShareAccess.Read,
            CreateDisposition.FILE_OVERWRITE,
            CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        Assert.Equal(FileStatus.FILE_OVERWRITTEN, fileStatus);

        _sut.CloseFile(handle);

        // Datei sollte jetzt leer sein (truncated)
        Assert.Equal(0, new FileInfo(Path.Combine(_testRoot, "tooverwrite.txt")).Length);
    }

    // ===================== CreateFile — Verzeichnisse =====================

    [Fact]
    public void CreateFile_NewDirectory_CreatesAndReturnsSuccess()
    {
        var status = _sut.CreateFile(
            out var handle, out var fileStatus,
            "newdir",
            AccessMask.GENERIC_READ,
            FileAttributes.Directory,
            ShareAccess.Read,
            CreateDisposition.FILE_CREATE,
            CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        Assert.Equal(FileStatus.FILE_CREATED, fileStatus);
        Assert.True(Directory.Exists(Path.Combine(_testRoot, "newdir")));

        _sut.CloseFile(handle);
    }

    [Fact]
    public void CreateFile_ExistingDirectory_FileCreate_ReturnsCollision()
    {
        CreateTestDirectory("existingdir");

        var status = _sut.CreateFile(
            out var handle, out var fileStatus,
            "existingdir",
            AccessMask.GENERIC_READ,
            FileAttributes.Directory,
            ShareAccess.Read,
            CreateDisposition.FILE_CREATE,
            CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        Assert.Equal(NTStatus.STATUS_OBJECT_NAME_COLLISION, status);
    }

    [Fact]
    public void CreateFile_OpenExistingDirectory_ReturnsSuccess()
    {
        CreateTestDirectory("opendir");

        var status = _sut.CreateFile(
            out var handle, out var fileStatus,
            "opendir",
            AccessMask.GENERIC_READ,
            FileAttributes.Directory,
            ShareAccess.Read,
            CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        Assert.Equal(FileStatus.FILE_OPENED, fileStatus);

        _sut.CloseFile(handle);
    }

    [Fact]
    public void CreateFile_OpenNonExistingDirectory_ReturnsNotFound()
    {
        var status = _sut.CreateFile(
            out var handle, out var fileStatus,
            "nodir",
            AccessMask.GENERIC_READ,
            FileAttributes.Directory,
            ShareAccess.Read,
            CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        Assert.Equal(NTStatus.STATUS_OBJECT_PATH_NOT_FOUND, status);
    }

    [Fact]
    public void CreateFile_RootPath_OpensAsDirectory()
    {
        // Leerer Pfad oder "\" sollte Root-Verzeichnis öffnen
        var status = _sut.CreateFile(
            out var handle, out var fileStatus,
            "",
            AccessMask.GENERIC_READ,
            FileAttributes.Directory,
            ShareAccess.Read,
            CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        _sut.CloseFile(handle);
    }

    // ===================== CreateFile — Berechtigungen =====================

    [Fact]
    public void CreateFile_NoUser_ReturnsError()
    {
        _sut.CurrentUser = null;

        Assert.Throws<InvalidOperationException>(() =>
        {
            _sut.CreateFile(
                out var handle, out var fileStatus,
                "noaccess.txt",
                AccessMask.GENERIC_WRITE,
                FileAttributes.Normal,
                ShareAccess.Read,
                CreateDisposition.FILE_CREATE,
                CreateOptions.FILE_NON_DIRECTORY_FILE,
                CreateSecurityContext());
        });
    }

    [Fact]
    public void CreateFile_WriteAccessDenied_ReturnsAccessDenied()
    {
        _fileServiceMock.Setup(f => f.CanWriteAsync(It.IsAny<string>(), It.IsAny<UserContext>())).ReturnsAsync(false);
        _fileServiceMock.Setup(f => f.CanCreateAsync(It.IsAny<string>(), It.IsAny<UserContext>())).ReturnsAsync(false);

        var status = _sut.CreateFile(
            out var handle, out var fileStatus,
            "denied.txt",
            AccessMask.GENERIC_WRITE,
            FileAttributes.Normal,
            ShareAccess.Read,
            CreateDisposition.FILE_CREATE,
            CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        Assert.Equal(NTStatus.STATUS_ACCESS_DENIED, status);
    }

    [Fact]
    public void CreateFile_ReadAccessDenied_ForOpenExisting_ReturnsAccessDenied()
    {
        CreateTestFile("readonly_denied.txt");
        _fileServiceMock.Setup(f => f.CanReadAsync(It.IsAny<string>(), It.IsAny<UserContext>())).ReturnsAsync(false);

        var status = _sut.CreateFile(
            out var handle, out var fileStatus,
            "readonly_denied.txt",
            AccessMask.GENERIC_READ,
            FileAttributes.Normal,
            ShareAccess.Read,
            CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        Assert.Equal(NTStatus.STATUS_ACCESS_DENIED, status);
    }

    // ===================== ReadFile / WriteFile =====================

    [Fact]
    public void ReadFile_ValidHandle_ReturnsData()
    {
        CreateTestFile("readable.txt", new byte[] { 10, 20, 30, 40, 50 });

        _sut.CreateFile(out var handle, out _, "readable.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var status = _sut.ReadFile(out var data, handle, 0, 5);

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        Assert.Equal(new byte[] { 10, 20, 30, 40, 50 }, data);

        _sut.CloseFile(handle);
    }

    [Fact]
    public void ReadFile_WithOffset_ReadsFromPosition()
    {
        CreateTestFile("offset.txt", new byte[] { 1, 2, 3, 4, 5 });

        _sut.CreateFile(out var handle, out _, "offset.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var status = _sut.ReadFile(out var data, handle, 2, 3);

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        Assert.Equal(new byte[] { 3, 4, 5 }, data);

        _sut.CloseFile(handle);
    }

    [Fact]
    public void ReadFile_AtEndOfFile_ReturnsEndOfFile()
    {
        CreateTestFile("eof.txt", new byte[] { 1, 2, 3 });

        _sut.CreateFile(out var handle, out _, "eof.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var status = _sut.ReadFile(out var data, handle, 3, 10);

        Assert.Equal(NTStatus.STATUS_END_OF_FILE, status);
        Assert.Empty(data);

        _sut.CloseFile(handle);
    }

    [Fact]
    public void ReadFile_AccessDenied_ReturnsAccessDenied()
    {
        CreateTestFile("no_read.txt", new byte[] { 1, 2, 3 });

        // Erlaube das Öffnen, aber verweigere das Lesen danach
        _fileServiceMock.Setup(f => f.CanReadAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(false);

        // Öffnen muss trotzdem funktionieren für diesen Test — 
        // wir setzen Read temporär auf true für CreateFile, dann auf false
        _fileServiceMock.SetupSequence(f => f.CanReadAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(true)   // für CreateFile
            .ReturnsAsync(false); // für ReadFile

        _sut.CreateFile(out var handle, out _, "no_read.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var status = _sut.ReadFile(out var data, handle, 0, 3);

        Assert.Equal(NTStatus.STATUS_ACCESS_DENIED, status);

        _sut.CloseFile(handle);
    }

    [Fact]
    public void ReadFile_DirectoryHandle_ReturnsInvalidHandle()
    {
        CreateTestDirectory("readdir");

        _sut.CreateFile(out var handle, out _, "readdir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        var status = _sut.ReadFile(out var data, handle, 0, 10);

        Assert.Equal(NTStatus.STATUS_INVALID_HANDLE, status);

        _sut.CloseFile(handle);
    }

    [Fact]
    public void WriteFile_ValidHandle_WritesData()
    {
        _sut.CreateFile(out var handle, out _, "writable.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var data = new byte[] { 100, 200, 255 };
        var status = _sut.WriteFile(out var written, handle, 0, data);

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        Assert.Equal(3, written);

        _sut.CloseFile(handle);

        Assert.Equal(data, File.ReadAllBytes(Path.Combine(_testRoot, "writable.txt")));
    }

    [Fact]
    public void WriteFile_WithOffset_WritesAtPosition()
    {
        CreateTestFile("writeoffset.txt", new byte[] { 0, 0, 0, 0, 0 });

        _sut.CreateFile(out var handle, out _, "writeoffset.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var status = _sut.WriteFile(out var written, handle, 2, new byte[] { 9, 8, 7 });

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        Assert.Equal(3, written);

        _sut.CloseFile(handle);

        Assert.Equal(new byte[] { 0, 0, 9, 8, 7 }, File.ReadAllBytes(Path.Combine(_testRoot, "writeoffset.txt")));
    }

    // ===================== CloseFile =====================

    [Fact]
    public void CloseFile_NullHandle_ReturnsInvalidHandle()
    {
        var status = _sut.CloseFile(null!);
        Assert.Equal(NTStatus.STATUS_INVALID_HANDLE, status);
    }

    [Fact]
    public void CloseFile_DeleteOnClose_DeletesFile()
    {
        CreateTestFile("todelete.txt");

        _sut.CreateFile(out var handle, out _, "todelete.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        // SetFileInformation mit DeleteOnClose
        _sut.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
        _sut.CloseFile(handle);

        Assert.False(File.Exists(Path.Combine(_testRoot, "todelete.txt")));
    }

    [Fact]
    public void CloseFile_DeleteOnClose_DeletesDirectory()
    {
        CreateTestDirectory("dirdelete");

        _sut.CreateFile(out var handle, out _, "dirdelete",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        _sut.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
        _sut.CloseFile(handle);

        Assert.False(Directory.Exists(Path.Combine(_testRoot, "dirdelete")));
    }

    // ===================== QueryDirectory =====================

    [Fact]
    public void QueryDirectory_ListsFilesAndDirs()
    {
        CreateTestDirectory("listdir");
        CreateTestDirectory("listdir/subdir");
        CreateTestFile("listdir/file1.txt");
        CreateTestFile("listdir/file2.txt");

        _sut.CreateFile(out var handle, out _, "listdir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        var status = _sut.QueryDirectory(out var result, handle, "*",
            FileInformationClass.FileDirectoryInformation);

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);

        var names = result.Select(r => ((FileDirectoryInformation)r).FileName).ToList();

        // "." und ".." plus 1 subdir + 2 files = 5
        Assert.Contains(".", names);
        Assert.Contains("..", names);
        Assert.Contains("subdir", names);
        Assert.Contains("file1.txt", names);
        Assert.Contains("file2.txt", names);
        Assert.Equal(5, names.Count);

        _sut.CloseFile(handle);
    }

    [Fact]
    public void QueryDirectory_WithPattern_FiltersResults()
    {
        CreateTestDirectory("filterdir");
        CreateTestFile("filterdir/doc.txt");
        CreateTestFile("filterdir/image.png");
        CreateTestFile("filterdir/notes.txt");

        _sut.CreateFile(out var handle, out _, "filterdir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        var status = _sut.QueryDirectory(out var result, handle, "*.txt",
            FileInformationClass.FileDirectoryInformation);

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);

        var names = result.Select(r => ((FileDirectoryInformation)r).FileName).ToList();
        Assert.Contains("doc.txt", names);
        Assert.Contains("notes.txt", names);
        Assert.DoesNotContain("image.png", names);
        // Kein "." / ".." bei spezifischem Pattern
        Assert.DoesNotContain(".", names);

        _sut.CloseFile(handle);
    }

    [Fact]
    public void QueryDirectory_EmptyDir_ReturnsDotEntries()
    {
        CreateTestDirectory("emptydir");

        _sut.CreateFile(out var handle, out _, "emptydir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        var status = _sut.QueryDirectory(out var result, handle, "*",
            FileInformationClass.FileDirectoryInformation);

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);

        var names = result.Select(r => ((FileDirectoryInformation)r).FileName).ToList();
        Assert.Equal(2, names.Count); // nur "." und ".."
        Assert.Contains(".", names);
        Assert.Contains("..", names);

        _sut.CloseFile(handle);
    }

    [Fact]
    public void QueryDirectory_NoMatch_ReturnsNoSuchFile()
    {
        CreateTestDirectory("nomatchdir");
        CreateTestFile("nomatchdir/readme.md");

        _sut.CreateFile(out var handle, out _, "nomatchdir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        var status = _sut.QueryDirectory(out var result, handle, "*.xyz",
            FileInformationClass.FileDirectoryInformation);

        Assert.Equal(NTStatus.STATUS_NO_SUCH_FILE, status);

        _sut.CloseFile(handle);
    }

    [Fact]
    public void QueryDirectory_FileHandle_ReturnsInvalidHandle()
    {
        CreateTestFile("notadir.txt");

        _sut.CreateFile(out var handle, out _, "notadir.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var status = _sut.QueryDirectory(out var result, handle, "*",
            FileInformationClass.FileDirectoryInformation);

        Assert.Equal(NTStatus.STATUS_INVALID_HANDLE, status);

        _sut.CloseFile(handle);
    }

    // ===================== GetFileInformation =====================

    [Fact]
    public void GetFileInformation_File_BasicInfo_ReturnsCorrectAttributes()
    {
        CreateTestFile("info.txt", new byte[] { 1, 2, 3 });

        _sut.CreateFile(out var handle, out _, "info.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var status = _sut.GetFileInformation(out var result, handle,
            FileInformationClass.FileBasicInformation);

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        var basic = Assert.IsType<FileBasicInformation>(result);
        Assert.Equal(FileAttributes.Normal, basic.FileAttributes);

        _sut.CloseFile(handle);
    }

    [Fact]
    public void GetFileInformation_Directory_BasicInfo_ReturnsDirectoryAttribute()
    {
        CreateTestDirectory("infodir");

        _sut.CreateFile(out var handle, out _, "infodir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        var status = _sut.GetFileInformation(out var result, handle,
            FileInformationClass.FileBasicInformation);

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        var basic = Assert.IsType<FileBasicInformation>(result);
        Assert.Equal(FileAttributes.Directory, basic.FileAttributes);

        _sut.CloseFile(handle);
    }

    [Fact]
    public void GetFileInformation_File_StandardInfo_ReturnsSize()
    {
        var content = new byte[] { 1, 2, 3, 4, 5 };
        CreateTestFile("sized.txt", content);

        _sut.CreateFile(out var handle, out _, "sized.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var status = _sut.GetFileInformation(out var result, handle,
            FileInformationClass.FileStandardInformation);

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        var std = Assert.IsType<FileStandardInformation>(result);
        Assert.Equal(5, std.EndOfFile);
        Assert.False(std.Directory);

        _sut.CloseFile(handle);
    }

    [Fact]
    public void GetFileInformation_Directory_StandardInfo_IsDirectory()
    {
        CreateTestDirectory("stddir");

        _sut.CreateFile(out var handle, out _, "stddir",
            AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        var status = _sut.GetFileInformation(out var result, handle,
            FileInformationClass.FileStandardInformation);

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        var std = Assert.IsType<FileStandardInformation>(result);
        Assert.True(std.Directory);
        Assert.Equal(0, std.EndOfFile);

        _sut.CloseFile(handle);
    }

    // ===================== SetFileInformation — Rename =====================

    [Fact]
    public void SetFileInformation_Rename_MovesFile()
    {
        CreateTestFile("before.txt");

        _sut.CreateFile(out var handle, out _, "before.txt",
            AccessMask.GENERIC_ALL, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var rename = new FileRenameInformationType2
        {
            FileName = "after.txt",
            ReplaceIfExists = false
        };

        var status = _sut.SetFileInformation(handle, rename);

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        Assert.False(File.Exists(Path.Combine(_testRoot, "before.txt")));
        Assert.True(File.Exists(Path.Combine(_testRoot, "after.txt")));

        _sut.CloseFile(handle);
    }

    [Fact]
    public void SetFileInformation_Rename_TargetExists_NoReplace_ReturnsCollision()
    {
        CreateTestFile("source.txt");
        CreateTestFile("target.txt");

        _sut.CreateFile(out var handle, out _, "source.txt",
            AccessMask.GENERIC_ALL, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var rename = new FileRenameInformationType2
        {
            FileName = "target.txt",
            ReplaceIfExists = false
        };

        var status = _sut.SetFileInformation(handle, rename);

        Assert.Equal(NTStatus.STATUS_OBJECT_NAME_COLLISION, status);

        _sut.CloseFile(handle);
    }

    [Fact]
    public void SetFileInformation_RenameDirectory_MovesDirectory()
    {
        CreateTestDirectory("olddir");

        _sut.CreateFile(out var handle, out _, "olddir",
            AccessMask.GENERIC_ALL, FileAttributes.Directory, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
            CreateSecurityContext());

        var rename = new FileRenameInformationType2
        {
            FileName = "newdir",
            ReplaceIfExists = false
        };

        var status = _sut.SetFileInformation(handle, rename);

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        Assert.False(Directory.Exists(Path.Combine(_testRoot, "olddir")));
        Assert.True(Directory.Exists(Path.Combine(_testRoot, "newdir")));

        _sut.CloseFile(handle);
    }

    // ===================== GetFileSystemInformation =====================

    [Fact]
    public void GetFileSystemInformation_Volume_ReturnsKaimoLabel()
    {
        var status = _sut.GetFileSystemInformation(out var result,
            FileSystemInformationClass.FileFsVolumeInformation);

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        var vol = Assert.IsType<FileFsVolumeInformation>(result);
        Assert.Equal("KaimoSMB", vol.VolumeLabel);
    }

    [Fact]
    public void GetFileSystemInformation_Attribute_ReportsNTFS()
    {
        var status = _sut.GetFileSystemInformation(out var result,
            FileSystemInformationClass.FileFsAttributeInformation);

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        var attr = Assert.IsType<FileFsAttributeInformation>(result);
        Assert.Equal("NTFS", attr.FileSystemName);
        Assert.Equal((double)255, attr.MaximumComponentNameLength);
    }

    [Fact]
    public void GetFileSystemInformation_Device_ReturnsDisk()
    {
        var status = _sut.GetFileSystemInformation(out var result,
            FileSystemInformationClass.FileFsDeviceInformation);

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        var dev = Assert.IsType<FileFsDeviceInformation>(result);
        Assert.Equal(DeviceType.Disk, dev.DeviceType);
    }

    // ===================== Security Stubs =====================

    [Fact]
    public void GetSecurityInformation_ReturnsEmptyDescriptor()
    {
        CreateTestFile("sec.txt");

        _sut.CreateFile(out var handle, out _, "sec.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var status = _sut.GetSecurityInformation(out var result, handle, SecurityInformation.OWNER_SECURITY_INFORMATION);

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);
        Assert.NotNull(result);

        _sut.CloseFile(handle);
    }

    [Fact]
    public void SetSecurityInformation_AcceptsAndIgnores()
    {
        CreateTestFile("setsec.txt");

        _sut.CreateFile(out var handle, out _, "setsec.txt",
            AccessMask.GENERIC_READ, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        var status = _sut.SetSecurityInformation(handle, SecurityInformation.OWNER_SECURITY_INFORMATION, new SecurityDescriptor());

        Assert.Equal(NTStatus.STATUS_SUCCESS, status);

        _sut.CloseFile(handle);
    }

    // ===================== FlushFileBuffers =====================

    [Fact]
    public void FlushFileBuffers_ValidHandle_ReturnsSuccess()
    {
        _sut.CreateFile(out var handle, out _, "flush.txt",
            AccessMask.GENERIC_WRITE, FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE,
            CreateSecurityContext());

        _sut.WriteFile(out _, handle, 0, new byte[] { 1, 2, 3 });

        var status = _sut.FlushFileBuffers(handle);
        Assert.Equal(NTStatus.STATUS_SUCCESS, status);

        _sut.CloseFile(handle);
    }

    [Fact]
    public void FlushFileBuffers_NullHandle_ReturnsInvalidHandle()
    {
        var status = _sut.FlushFileBuffers(null!);
        Assert.Equal(NTStatus.STATUS_INVALID_HANDLE, status);
    }
}