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

    // ── Read / Write / Delete ──

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
        // Should be a no-op
        await _sut.DeleteAsync("ghost.txt");
    }

    // ── Metadata ──

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

    // ── Path Traversal ──

    [Fact]
    public async Task ReadAsync_PathTraversal_ThrowsUnauthorized()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.ReadAsync("../../etc/passwd"));
    }

    [Fact]
    public async Task WriteAsync_PathTraversal_ThrowsUnauthorized()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.WriteAsync("../../tmp/evil.txt", new MemoryStream([1])));
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

    // ── Overwrite ──

    [Fact]
    public async Task WriteAsync_OverwritesExistingFile()
    {
        await _sut.WriteAsync("overwrite.txt", new MemoryStream([1, 2, 3]));
        await _sut.WriteAsync("overwrite.txt", new MemoryStream([9, 8]));

        var content = await File.ReadAllBytesAsync(
            Path.Combine(_testRoot, "overwrite.txt"));
        Assert.Equal([9, 8], content);
    }

    // ── Large file handling ──

    [Fact]
    public async Task WriteAndRead_LargeFile_RoundTrips()
    {
        var data = new byte[1024 * 1024]; // 1 MB
        new Random(42).NextBytes(data);

        await _sut.WriteAsync("large.bin", new MemoryStream(data));

        using var readStream = await _sut.ReadAsync("large.bin");
        using var ms = new MemoryStream();
        await readStream.CopyToAsync(ms);
        Assert.Equal(data, ms.ToArray());
    }

    // ── Empty file ──

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

    // ── Deeply nested paths ──

    [Fact]
    public async Task WriteAsync_DeeplyNestedPath_CreatesAllDirectories()
    {
        var path = "a/b/c/d/e/f/deep.txt";
        await _sut.WriteAsync(path, new MemoryStream([42]));

        Assert.True(File.Exists(Path.Combine(_testRoot, path)));
    }

    // ── Special characters in filenames ──

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

    // ── Delete idempotency ──

    [Fact]
    public async Task DeleteAsync_CalledTwice_DoesNotThrow()
    {
        await _sut.WriteAsync("twice.txt", new MemoryStream([1]));
        await _sut.DeleteAsync("twice.txt");
        await _sut.DeleteAsync("twice.txt"); // should not throw
    }

    // ── Metadata for nested paths ──

    [Fact]
    public async Task GetMetadataAsync_NestedFile_ReturnsCorrectSize()
    {
        var data = new byte[777];
        await _sut.WriteAsync("sub/nested.bin", new MemoryStream(data));

        var meta = await _sut.GetMetadataAsync("sub/nested.bin");
        Assert.Equal(777, meta.Size);
    }

    // ── Path traversal with encoded/tricky patterns ──

    [Theory]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("sub/../../..")]
    [InlineData("./../../etc/shadow")]
    public async Task WriteAsync_PathTraversal_Variants_ThrowsUnauthorized(string path)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.WriteAsync(path, new MemoryStream([1])));
    }

    // ── Concurrent writes to different files ──

    [Fact]
    public async Task ConcurrentWrites_DifferentFiles_AllSucceed()
    {
        var tasks = Enumerable.Range(0, 10).Select(i =>
            _sut.WriteAsync($"concurrent_{i}.txt",
                new MemoryStream(BitConverter.GetBytes(i))));

        await Task.WhenAll(tasks);

        for (int i = 0; i < 10; i++)
        {
            Assert.True(File.Exists(Path.Combine(_testRoot, $"concurrent_{i}.txt")));
        }
    }
}

/// <summary>
/// Testable wrapper that exposes the internal FileSystemStorage
/// constructor without IServiceProvider (no DB dependency).
/// This uses reflection since FileSystemStorage is internal.
/// </summary>
public class FileSystemStorageTestable : Kaimo_File_Server.Core.Storage.IStorageEngine
{
    private readonly object _inner;
    private readonly Type _type;

    public FileSystemStorageTestable(string rootPath)
    {
        _type = typeof(Kaimo_File_Server.Infrastructure.ServiceCollectionExtensions)
            .Assembly
            .GetType("Kaimo_File_Server.Infrastructure.Storage.FileSystemStorage")!;

        // Use the constructor that takes only string (no IServiceProvider)
        _inner = Activator.CreateInstance(_type,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Public,
            null, [rootPath], null)!;
    }

    public Task<Stream> ReadAsync(string path)
        => Invoke<Stream>("ReadAsync", path);

    public Task WriteAsync(string path, Stream data)
        => Invoke("WriteAsync", path, data);

    public Task DeleteAsync(string path)
        => Invoke("DeleteAsync", path);

    public Task<FileMetadata> GetMetadataAsync(string path)
        => Invoke<FileMetadata>("GetMetadataAsync", path);

    // ── Helpers that unwrap TargetInvocationException from reflection ──

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
            throw; // unreachable, keeps compiler happy
        }
    }
}