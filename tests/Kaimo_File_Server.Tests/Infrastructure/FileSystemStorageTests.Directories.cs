using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Infrastructure.Storage;
using Xunit;

namespace Kaimo_File_Server.Tests;

public partial class FileSystemStorageTests
{
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
