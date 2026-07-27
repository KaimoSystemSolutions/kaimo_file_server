using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Infrastructure.Storage;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Tests for the FileSystemStorage engine.
/// Uses the internal constructor (no IServiceProvider) to test
/// pure filesystem operations without a database.
/// </summary>
public class FileSystemStorageTests : IDisposable
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

    // ═══════════════════ Metadata ═══════════════════

    [Fact]
    public async Task GetMetadataAsync_File_ReturnsCorrectInfo()
    {
        File.WriteAllBytes(Path.Combine(_testRoot, "meta.txt"), new byte[512]);

        var meta = await _sut.GetMetadataAsync("meta.txt");
        Assert.Equal("meta.txt", meta.Path);
        Assert.Equal("meta.txt", meta.Name);
        Assert.Equal(512, meta.Size);
        Assert.False(meta.IsDirectory);
    }

    [Fact]
    public async Task GetMetadataAsync_Directory_ReturnsIsDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_testRoot, "metadir"));

        var meta = await _sut.GetMetadataAsync("metadir");
        Assert.True(meta.IsDirectory);
    }

    [Fact]
    public async Task GetMetadataAsync_NonExistentPath_ReturnsZeroSize()
    {
        var meta = await _sut.GetMetadataAsync("nofile.txt");
        Assert.Equal(0, meta.Size);
        Assert.False(meta.IsDirectory);
    }

    [Fact]
    public async Task GetMetadataAsync_WithoutDbProvider_ReturnsEmptyAcl()
    {
        File.WriteAllBytes(Path.Combine(_testRoot, "noacl.txt"), [1]);
        var meta = await _sut.GetMetadataAsync("noacl.txt");
        Assert.NotNull(meta.Acl);
        Assert.Empty(meta.Acl);
    }

    // ═══════════════════ Path Traversal ═══════════════════

    [Fact]
    public async Task ReadAsync_PathTraversal_ThrowsUnauthorized()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.ReadAsync("../../etc/passwd"));
    }

    [Fact]
    public async Task DeleteAsync_PathTraversal_ThrowsUnauthorized()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.DeleteAsync("../../tmp/evil.txt"));
    }

    [Fact]
    public async Task GetMetadataAsync_PathTraversal_ThrowsUnauthorized()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.GetMetadataAsync("../../../etc/shadow"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("sub/../../..")]
    [InlineData("./../../etc/shadow")]
    [InlineData("../../tmp/evil.txt")]
    public async Task WriteAsync_PathTraversal_Variants_ThrowsUnauthorized(string path)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.WriteAsync(path, new MemoryStream([1])));
    }

    // ═══════════════════ Overwrite ═══════════════════

    [Fact]
    public async Task WriteAsync_OverwritesExistingFile()
    {
        await _sut.WriteAsync("overwrite.txt", new MemoryStream([1, 2, 3]));
        await _sut.WriteAsync("overwrite.txt", new MemoryStream([9, 8]));

        var content = await File.ReadAllBytesAsync(
            Path.Combine(_testRoot, "overwrite.txt"));
        Assert.Equal([9, 8], content);
    }

    // ═══════════════════ Large file ═══════════════════

    [Fact]
    public async Task WriteAndRead_LargeFile_RoundTrips()
    {
        var data = new byte[1024 * 1024];
        new Random(42).NextBytes(data);

        await _sut.WriteAsync("large.bin", new MemoryStream(data));

        using var readStream = await _sut.ReadAsync("large.bin");
        using var ms = new MemoryStream();
        await readStream.CopyToAsync(ms);
        Assert.Equal(data, ms.ToArray());
    }

    // ═══════════════════ Empty file ═══════════════════

    [Fact]
    public async Task WriteAsync_EmptyStream_CreatesEmptyFile()
    {
        await _sut.WriteAsync("empty.txt", new MemoryStream([]));

        var info = new FileInfo(Path.Combine(_testRoot, "empty.txt"));
        Assert.True(info.Exists);
        Assert.Equal(0, info.Length);
    }

    [Fact]
    public async Task GetMetadataAsync_EmptyFile_ReturnsZeroSize()
    {
        await _sut.WriteAsync("zero.txt", new MemoryStream([]));
        var meta = await _sut.GetMetadataAsync("zero.txt");
        Assert.Equal(0, meta.Size);
        Assert.False(meta.IsDirectory);
    }

    // ═══════════════════ Deeply nested paths ═══════════════════

    [Fact]
    public async Task WriteAsync_DeeplyNestedPath_CreatesAllDirectories()
    {
        var path = "a/b/c/d/e/f/deep.txt";
        await _sut.WriteAsync(path, new MemoryStream([42]));
        Assert.True(File.Exists(Path.Combine(_testRoot, path)));
    }

    // ═══════════════════ Special characters ═══════════════════

    [Fact]
    public async Task WriteAndRead_SpecialCharsInName_RoundTrips()
    {
        var name = "file with spaces & (parens).txt";
        await _sut.WriteAsync(name, new MemoryStream([1, 2, 3]));

        using var stream = await _sut.ReadAsync(name);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        Assert.Equal([1, 2, 3], ms.ToArray());
    }

    // ═══════════════════ Delete idempotency ═══════════════════

    [Fact]
    public async Task DeleteAsync_CalledTwice_DoesNotThrow()
    {
        await _sut.WriteAsync("twice.txt", new MemoryStream([1]));
        await _sut.DeleteAsync("twice.txt");
        await _sut.DeleteAsync("twice.txt");
    }

    // ═══════════════════ Metadata for nested paths ═══════════════════

    [Fact]
    public async Task GetMetadataAsync_NestedFile_ReturnsCorrectSize()
    {
        var data = new byte[777];
        await _sut.WriteAsync("sub/nested.bin", new MemoryStream(data));

        var meta = await _sut.GetMetadataAsync("sub/nested.bin");
        Assert.Equal(777, meta.Size);
    }

    // ═══════════════════ Concurrent writes ═══════════════════

    [Fact]
    public async Task ConcurrentWrites_DifferentFiles_AllSucceed()
    {
        var tasks = Enumerable.Range(0, 10).Select(i =>
            _sut.WriteAsync($"concurrent_{i}.txt",
                new MemoryStream(BitConverter.GetBytes(i))));

        await Task.WhenAll(tasks);

        for (int i = 0; i < 10; i++)
            Assert.True(File.Exists(Path.Combine(_testRoot, $"concurrent_{i}.txt")));
    }

    // ═══════════════════ CreateDirectory (sync-style) ═══════════════════

    [Fact]
    public async Task CreateDirectory_CreatesDirectory()
    {
        await _sut.CreateDirectory("newdir");
        Assert.True(Directory.Exists(Path.Combine(_testRoot, "newdir")));
    }

    [Fact]
    public async Task CreateDirectory_CreatesNestedDirectories()
    {
        await _sut.CreateDirectory("parent/child/grandchild");
        Assert.True(Directory.Exists(
            Path.Combine(_testRoot, "parent", "child", "grandchild")));
    }

    [Fact]
    public async Task CreateDirectory_AlreadyExists_DoesNotThrow()
    {
        Directory.CreateDirectory(Path.Combine(_testRoot, "existing"));
        await _sut.CreateDirectory("existing");
        Assert.True(Directory.Exists(Path.Combine(_testRoot, "existing")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("sub/../../..")]
    [InlineData("./../../etc")]
    [InlineData("../../etc/evil")]
    public async Task CreateDirectory_PathTraversal_Variants_ThrowsUnauthorized(string path)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.CreateDirectory(path));
    }

    [Fact]
    public async Task CreateDirectory_ThenWriteFile_Works()
    {
        await _sut.CreateDirectory("prepared");
        await _sut.WriteAsync("prepared/file.txt", new MemoryStream([1, 2, 3]));
        Assert.True(File.Exists(Path.Combine(_testRoot, "prepared", "file.txt")));
    }

    [Fact]
    public async Task CreateDirectory_ThenGetMetadata_ReturnsIsDirectory()
    {
        await _sut.CreateDirectory("metacheck");
        var meta = await _sut.GetMetadataAsync("metacheck");
        Assert.True(meta.IsDirectory);
    }

    [Fact]
    public async Task DeleteAsync_RemovesCreatedDirectory()
    {
        await _sut.CreateDirectory("removeme");
        await _sut.WriteAsync("removeme/child.txt", new MemoryStream([1]));
        await _sut.DeleteAsync("removeme");
        Assert.False(Directory.Exists(Path.Combine(_testRoot, "removeme")));
    }

    // ═══════════════════ CreateDirectoryAsync ═══════════════════

    [Fact]
    public async Task CreateDirectoryAsync_CreatesDirectory()
    {
        await _sut.CreateDirectoryAsync("asyncdir");
        Assert.True(Directory.Exists(Path.Combine(_testRoot, "asyncdir")));
    }

    [Fact]
    public async Task CreateDirectoryAsync_CreatesNestedDirectories()
    {
        await _sut.CreateDirectoryAsync("a_parent/a_child/a_grandchild");
        Assert.True(Directory.Exists(
            Path.Combine(_testRoot, "a_parent", "a_child", "a_grandchild")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("sub/../../..")]
    [InlineData("./../../etc")]
    public async Task CreateDirectoryAsync_PathTraversal_ThrowsUnauthorized(string path)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.CreateDirectoryAsync(path));
    }

    // ═══════════════════ IsDirectoryAsync ═══════════════════

    [Fact]
    public async Task IsDirectoryAsync_ExistingDirectory_ReturnsTrue()
    {
        Directory.CreateDirectory(Path.Combine(_testRoot, "realdir"));
        Assert.True(await _sut.IsDirectoryAsync("realdir"));
    }

    [Fact]
    public async Task IsDirectoryAsync_ExistingFile_ReturnsFalse()
    {
        File.WriteAllBytes(Path.Combine(_testRoot, "afile.txt"), [1]);
        Assert.False(await _sut.IsDirectoryAsync("afile.txt"));
    }

    [Fact]
    public async Task IsDirectoryAsync_NonExistentPath_ReturnsFalse()
    {
        Assert.False(await _sut.IsDirectoryAsync("doesnotexist"));
    }

    [Fact]
    public async Task IsDirectoryAsync_NestedDirectory_ReturnsTrue()
    {
        await _sut.CreateDirectoryAsync("outer/inner");
        Assert.True(await _sut.IsDirectoryAsync("outer/inner"));
        Assert.True(await _sut.IsDirectoryAsync("outer"));
    }

    // ═══════════════════ ListAsync ═══════════════════

    [Fact]
    public async Task ListAsync_EmptyDirectory_ReturnsEmptyList()
    {
        await _sut.CreateDirectoryAsync("emptydir");
        var items = await _sut.ListAsync("emptydir");
        Assert.Empty(items);
    }

    [Fact]
    public async Task ListAsync_NonExistentDirectory_ThrowsDirectoryNotFound()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => _sut.ListAsync("nope"));
    }

    [Fact]
    public async Task ListAsync_MixedContent_ReturnsFilesAndDirs()
    {
        await _sut.CreateDirectoryAsync("mixed/subdir");
        await _sut.WriteAsync("mixed/file1.txt", new MemoryStream([1]));
        await _sut.WriteAsync("mixed/file2.txt", new MemoryStream([2]));

        var items = await _sut.ListAsync("mixed");

        Assert.Equal(3, items.Count);
        Assert.Single(items, i => i.IsDirectory && i.Name == "subdir");
        Assert.Single(items, i => !i.IsDirectory && i.Name == "file1.txt");
        Assert.Single(items, i => !i.IsDirectory && i.Name == "file2.txt");
    }

    [Fact]
    public async Task ListAsync_FileEntries_HaveCorrectSize()
    {
        var data = new byte[256];
        await _sut.WriteAsync("sized/data.bin", new MemoryStream(data));

        var items = await _sut.ListAsync("sized");
        var file = Assert.Single(items);
        Assert.Equal(256, file.Size);
        Assert.False(file.IsDirectory);
    }

    // ═══════════════════ Edge Cases ═══════════════════

    [Fact]
    public async Task ReadAsync_Directory_ThrowsOrFails()
    {
        // Reading a directory as a file should not succeed silently
        await _sut.CreateDirectoryAsync("cantread");
        await Assert.ThrowsAnyAsync<Exception>(
            () => _sut.ReadAsync("cantread"));
    }

    [Fact]
    public async Task WriteAsync_OverwriteShorterContent_TruncatesFile()
    {
        // Ensures FileMode.Create truncates — no leftover bytes from prior write
        await _sut.WriteAsync("trunc.txt", new MemoryStream(new byte[1000]));
        await _sut.WriteAsync("trunc.txt", new MemoryStream([42]));

        var content = await File.ReadAllBytesAsync(Path.Combine(_testRoot, "trunc.txt"));
        Assert.Single(content);
        Assert.Equal(42, content[0]);
    }

    [Fact]
    public async Task GetMetadataAsync_ReturnsReasonableTimestamps()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        await _sut.WriteAsync("timed.txt", new MemoryStream([1]));
        var after = DateTime.UtcNow.AddSeconds(1);

        var meta = await _sut.GetMetadataAsync("timed.txt");
        Assert.InRange(meta.CreatedAt, before, after);
        Assert.InRange(meta.ModifiedAt, before, after);
    }

    [Fact]
    public async Task ListAsync_DoesNotReturnNestedItems()
    {
        // ListAsync should only return direct children, not grandchildren
        await _sut.WriteAsync("parent/child/grandchild.txt", new MemoryStream([1]));
        await _sut.WriteAsync("parent/direct.txt", new MemoryStream([2]));

        var items = await _sut.ListAsync("parent");
        Assert.Equal(2, items.Count); // "child" dir + "direct.txt" file
        Assert.DoesNotContain(items, i => i.Name == "grandchild.txt");
    }

    // ═══════════════════ Move ═══════════════════

    [Fact]
    public async Task MoveAsync_WithoutCollision_ReturnsRequestedPath()
    {
        await _sut.WriteAsync("src.txt", new MemoryStream([1, 2, 3]));

        var actual = await _sut.MoveAsync("src.txt", "bin/src.txt");

        Assert.Equal("bin/src.txt", actual);
        Assert.True(File.Exists(Path.Combine(_testRoot, "bin", "src.txt")));
        Assert.False(File.Exists(Path.Combine(_testRoot, "src.txt")));
    }

    [Fact]
    public async Task MoveAsync_OnNameCollision_ReturnsSuffixedPathAndKeepsBoth()
    {
        // A file with the target name already exists → the move must not clobber it.
        await _sut.WriteAsync("bin/dup.txt", new MemoryStream([9]));
        await _sut.WriteAsync("dup.txt", new MemoryStream([1, 2, 3]));

        var actual = await _sut.MoveAsync("dup.txt", "bin/dup.txt");

        // Returned path is disambiguated and differs from the requested one...
        Assert.StartsWith("bin/dup.txt_", actual);
        Assert.NotEqual("bin/dup.txt", actual);

        // ...points at the file that actually landed on disk...
        Assert.True(File.Exists(Path.Combine(_testRoot, actual.Replace('/', Path.DirectorySeparatorChar))));

        // ...and the pre-existing file is untouched.
        Assert.Equal([9], await File.ReadAllBytesAsync(Path.Combine(_testRoot, "bin", "dup.txt")));
    }
}

/// <summary>
/// Testable wrapper that exposes the internal FileSystemStorage
/// constructor without IServiceProvider (no DB dependency).
/// This uses reflection since FileSystemStorage may be internal.
/// </summary>
public class FileSystemStorageTestable : Kaimo_File_Server.Core.Storage.IStorageEngine
{
    private readonly object _inner;
    private readonly Type _type;
    private readonly string _rootPath;

    public FileSystemStorageTestable(string rootPath)
    {
        this._rootPath = rootPath;
        
        _type = typeof(Kaimo_File_Server.Infrastructure.ServiceCollectionExtensions)
            .Assembly
            .GetType("Kaimo_File_Server.Infrastructure.Storage.FileSystemStorage")!;

        // Use the constructor that takes (string, Guid, IServiceProvider?)
        // Pass Guid.Empty and null to test without a database
        _inner = Activator.CreateInstance(_type,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Public,
            null, [rootPath, Guid.Empty, (IServiceProvider?)null], null)!;
    }

    public Task<Stream> ReadAsync(string path)
        => Invoke<Stream>("ReadAsync", path);

    public Task WriteAsync(string path, Stream data, CancellationToken cancellationToken = default)
        => Invoke("WriteAsync", path, data, cancellationToken);

    public Task CreateDirectory(string path)
        => Invoke("CreateDirectory", path);

    public Task DeleteAsync(string path)
        => Invoke("DeleteAsync", path);

    public Task<FileMetadata> GetMetadataAsync(string path)
        => Invoke<FileMetadata>("GetMetadataAsync", path);

    public Task CreateDirectoryAsync(string path)
        => Invoke("CreateDirectoryAsync", path);

    public Task<bool> IsDirectoryAsync(string path)
        => Invoke<bool>("IsDirectoryAsync", path);

    public Task<List<FileMetadata>> ListAsync(string directoryPath)
        => Invoke<List<FileMetadata>>("ListAsync", directoryPath);

    public Task<string> MoveAsync(string oldPath, string newPath)
        => Invoke<string>("MoveAsync", oldPath, newPath);

    public Task RenameFileAsync(string oldPath, string newPath)
        => Invoke("RenameFileAsync", oldPath, newPath);

    public Task RenameDirectoryAsync(string oldDirPath, string newDirPath)
        => Invoke("RenameDirectoryAsync", oldDirPath, newDirPath);

    public Task UnzipAsync(string zipPath, string targetPath)
        => Invoke("UnzipAsync", zipPath, targetPath);

    public Task ArchiveAsync(List<string> sourcePaths, string targetPath, string format)
        => Invoke("ArchiveAsync", sourcePaths, targetPath, format);

    public Task<long> GetDirectorySizeAsync(string relativePath)
        => Invoke<long>("GetDirectorySizeAsync", relativePath);

    public Task<bool> ExistsAsync(string path)
        => Invoke<bool>("ExistsAsync", path);

    public Task<Kaimo_File_Server.Core.Storage.IStorageHandle> OpenAsync(
        string path,
        Kaimo_File_Server.Core.Storage.OpenMode mode,
        Kaimo_File_Server.Core.Storage.AccessIntent intent,
        Kaimo_File_Server.Core.Storage.ShareIntent share,
        CancellationToken ct = default)
        => Invoke<Kaimo_File_Server.Core.Storage.IStorageHandle>(
            "OpenAsync", path, mode, intent, share, ct);


    public Task SetModifiedDateAsync(string path, DateTime time)
        => Invoke("SetModifiedDateAsync", path, time);

    
    // -- Helpers that unwrap TargetInvocationException from reflection --

    private async Task Invoke(string method, params object[] args)
    {
        try
        {
            await (Task)_type.GetMethod(method)!.Invoke(_inner, args)!;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    private async Task<T> Invoke<T>(string method, params object[] args)
    {
        try
        {
            return await (Task<T>)_type.GetMethod(method)!.Invoke(_inner, args)!;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // unreachable
        }
    }

    public string ToAbsolutePath(string shareRelativePath)
    {
        throw new NotImplementedException();
    }
}
