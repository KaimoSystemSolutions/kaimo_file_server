using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Infrastructure.Storage;
using Xunit;

namespace Kaimo_File_Server.Tests;

public partial class FileSystemStorageTests
{
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

    [Fact]
    public async Task WriteAsync_CancelledOverwrite_PreservesExistingFile()
    {
        var fullPath = Path.Combine(_testRoot, "preserved.txt");
        await File.WriteAllBytesAsync(fullPath, [1, 2, 3, 4]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _sut.WriteAsync("preserved.txt", new MemoryStream([9, 8]), cts.Token));

        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(fullPath));
        Assert.Empty(Directory.GetFiles(_testRoot, "*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_CancelledCreate_DoesNotPublishPartialFile()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _sut.WriteAsync("not-created.txt", new MemoryStream([9, 8]), cts.Token));

        Assert.False(File.Exists(Path.Combine(_testRoot, "not-created.txt")));
        Assert.Empty(Directory.GetFiles(_testRoot, "*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_OverwriteWhileOldVersionIsBeingRead_PublishesNewFile()
    {
        await _sut.WriteAsync("concurrent.txt", new MemoryStream([1, 2, 3]));
        await using var oldReader = await _sut.ReadAsync("concurrent.txt");

        await _sut.WriteAsync("concurrent.txt", new MemoryStream([9, 8]));

        Assert.Equal(
            [9, 8],
            await File.ReadAllBytesAsync(Path.Combine(_testRoot, "concurrent.txt")));
        using var oldCopy = new MemoryStream();
        await oldReader.CopyToAsync(oldCopy);
        Assert.Equal([1, 2, 3], oldCopy.ToArray());
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
}
