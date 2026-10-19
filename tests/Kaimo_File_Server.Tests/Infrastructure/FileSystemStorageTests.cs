using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Infrastructure.Storage;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Tests for the FileSystemStorage engine.
/// Uses the internal constructor (no IServiceProvider) to test
/// pure filesystem operations without a database.
/// </summary>
public partial class FileSystemStorageTests : IDisposable
{
    private readonly string _testRoot;
    private readonly FileSystemStorageTestable _sut;

    public FileSystemStorageTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(),
            "kaimo_storage_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _sut = new FileSystemStorageTestable(_testRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, true); } catch { }
    }

    // ═══════════════════ Read / Write / Delete ═══════════════════

    [Fact]
    public async Task WriteAsync_CreatesFileWithContent()
    {
        var data = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });
        await _sut.WriteAsync("test.txt", data);

        var fullPath = Path.Combine(_testRoot, "test.txt");
        Assert.True(File.Exists(fullPath));
        Assert.Equal([1, 2, 3, 4, 5], await File.ReadAllBytesAsync(fullPath));
    }

    [Fact]
    public async Task WriteAsync_AcceptsNamesUpToTheFileSystemLimit()
    {
        // The temporary file must not embed the name: "<name>" + 44 characters of suffix used
        // to exceed the 255-byte limit for every name over ~210 bytes.
        var name = new string('n', 250) + ".txt";

        await _sut.WriteAsync(name, new MemoryStream([1]));

        Assert.True(File.Exists(Path.Combine(_testRoot, name)));
        Assert.Single(Directory.GetFileSystemEntries(_testRoot));
    }

    [Fact]
    public async Task ReadAsync_PreservingAccessTime_RestoresItOnDispose()
    {
        var fullPath = Path.Combine(_testRoot, "atime.txt");
        File.WriteAllBytes(fullPath, [1, 2, 3]);
        var accessed = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastAccessTimeUtc(fullPath, accessed);
        var modified = File.GetLastWriteTimeUtc(fullPath);

        await using (var stream = await _sut.ReadAsync("atime.txt", preserveAccessTime: true))
            await stream.CopyToAsync(Stream.Null);

        Assert.Equal(accessed, File.GetLastAccessTimeUtc(fullPath));
        // Restoring atime must not disturb mtime: it is part of the size:mtime item tag.
        Assert.Equal(modified, File.GetLastWriteTimeUtc(fullPath));
    }

    [Fact]
    public async Task ReadAsync_PreservingAccessTime_FileReplacedDuringRead_KeepsTheNewFilesAccessTime()
    {
        if (OperatingSystem.IsWindows())
            return; // Windows restores by path (read-only handle); the production container is Linux.

        var fullPath = Path.Combine(_testRoot, "replaced.txt");
        File.WriteAllBytes(fullPath, [1, 2, 3]);
        File.SetLastAccessTimeUtc(fullPath, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var stream = await _sut.ReadAsync("replaced.txt", preserveAccessTime: true);
        // An atomic replace (temp + rename) while the internal read is still open.
        var newAccess = new DateTime(2020, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        var temp = Path.Combine(_testRoot, "replaced.tmp");
        File.WriteAllBytes(temp, [9]);
        File.SetLastAccessTimeUtc(temp, newAccess);
        File.Move(temp, fullPath, overwrite: true);
        await stream.DisposeAsync();

        // The restore hit the old inode, not the file that now lives at the path.
        Assert.Equal(newAccess, File.GetLastAccessTimeUtc(fullPath));
    }

    [Fact]
    public async Task ReadAsync_PreservingAccessTime_UndisposedStream_NeverRestoresFromTheFinalizer()
    {
        var fullPath = Path.Combine(_testRoot, "leaked.txt");
        File.WriteAllBytes(fullPath, [1, 2, 3]);
        File.SetLastAccessTimeUtc(fullPath, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await OpenAndAbandonAsync("leaked.txt");
        // A later stamp (sync or user access) must survive the abandoned stream's finalizer.
        var later = new DateTime(2020, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastAccessTimeUtc(fullPath, later);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        Assert.Equal(later, File.GetLastAccessTimeUtc(fullPath));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task OpenAndAbandonAsync(string path)
        => _ = await _sut.ReadAsync(path, preserveAccessTime: true);

    [Fact]
    public async Task SetModifiedDateAsync_StampsFilesAndDirectories_WithAccessTimeEqualToModified()
    {
        Directory.CreateDirectory(Path.Combine(_testRoot, "dir"));
        File.WriteAllBytes(Path.Combine(_testRoot, "dir", "f.txt"), [1]);
        var when = new DateTime(2010, 6, 7, 8, 9, 10, DateTimeKind.Utc);

        await _sut.SetModifiedDateAsync("dir/f.txt", when);
        await _sut.SetModifiedDateAsync("dir", when);

        Assert.Equal(when, File.GetLastWriteTimeUtc(Path.Combine(_testRoot, "dir", "f.txt")));
        Assert.Equal(when, File.GetLastAccessTimeUtc(Path.Combine(_testRoot, "dir", "f.txt")));
        Assert.Equal(when, Directory.GetLastWriteTimeUtc(Path.Combine(_testRoot, "dir")));
        Assert.Equal(when, Directory.GetLastAccessTimeUtc(Path.Combine(_testRoot, "dir")));
    }

    [Fact]
    public async Task WriteAsync_CreatesSubdirectories()
    {
        await _sut.WriteAsync("deep/nested/file.txt", new MemoryStream([42]));
        Assert.True(File.Exists(Path.Combine(_testRoot, "deep", "nested", "file.txt")));
    }

    [Fact]
    public async Task ReadAsync_ReturnsFileContent()
    {
        File.WriteAllBytes(Path.Combine(_testRoot, "read.txt"), [10, 20, 30]);

        using var stream = await _sut.ReadAsync("read.txt");
        var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        Assert.Equal([10, 20, 30], ms.ToArray());
    }

    [Fact]
    public async Task ReadAsync_NonExistentFile_ThrowsFileNotFound()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _sut.ReadAsync("nonexistent.txt"));
    }

    [Fact]
    public async Task DeleteAsync_RemovesFile()
    {
        var path = Path.Combine(_testRoot, "todelete.txt");
        File.WriteAllBytes(path, [1]);
        Assert.True(File.Exists(path));

        await _sut.DeleteAsync("todelete.txt");
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DeleteAsync_RemovesDirectoryRecursively()
    {
        var dir = Path.Combine(_testRoot, "deldir");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "child.txt"), [1]);

        await _sut.DeleteAsync("deldir");
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task DeleteAsync_NonExistentPath_DoesNotThrow()
    {
        await _sut.DeleteAsync("ghost.txt");
    }
}
