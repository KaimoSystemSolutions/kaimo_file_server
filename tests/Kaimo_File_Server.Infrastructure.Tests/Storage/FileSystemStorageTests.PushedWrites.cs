using Xunit;

namespace Kaimo_File_Server.Infrastructure.Tests.Storage;

/// <summary>Content pushed by a callback gets the same all-or-nothing guarantee as a stream.</summary>
public partial class FileSystemStorageTests
{
    private static Func<Stream, CancellationToken, Task> Writes(params byte[] content)
        => async (file, token) => await file.WriteAsync(content, token);

    private static Func<Stream, CancellationToken, Task> WritesThenFails(params byte[] content)
        => async (file, token) =>
        {
            await file.WriteAsync(content, token);
            throw new IOException("connection lost");
        };

    [Fact]
    public async Task PushedWrite_CreatesTheFileAndLeavesNoTemporaryFile()
    {
        await _sut.WriteAsync("new.bin", Writes(1, 2, 3));

        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(_testRoot, "new.bin")));
        Assert.Empty(Directory.GetFiles(_testRoot, "*.tmp"));
    }

    [Fact]
    public async Task PushedWrite_ReplacesAnExistingFile()
    {
        var fullPath = Path.Combine(_testRoot, "replaced.bin");
        await File.WriteAllBytesAsync(fullPath, [1, 2, 3, 4]);

        await _sut.WriteAsync("replaced.bin", Writes(9));

        Assert.Equal([9], await File.ReadAllBytesAsync(fullPath));
    }

    [Fact]
    public async Task PushedWrite_ExistingFileIsNotTouchedWhileContentArrives()
    {
        var fullPath = Path.Combine(_testRoot, "live.bin");
        await File.WriteAllBytesAsync(fullPath, [1, 2, 3, 4]);
        byte[]? duringWrite = null;

        await _sut.WriteAsync("live.bin", async (file, token) =>
        {
            await file.WriteAsync(new byte[] { 9 }, token);
            duringWrite = await File.ReadAllBytesAsync(fullPath, token);
        });

        Assert.Equal([1, 2, 3, 4], duringWrite);
    }

    [Fact]
    public async Task PushedWrite_WriterFails_PreservesTheExistingFile()
    {
        var fullPath = Path.Combine(_testRoot, "preserved.bin");
        await File.WriteAllBytesAsync(fullPath, [1, 2, 3, 4]);
        var modified = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(fullPath, modified);

        await Assert.ThrowsAsync<IOException>(() => _sut.WriteAsync("preserved.bin", WritesThenFails(9, 9)));

        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(fullPath));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(fullPath));
        Assert.Empty(Directory.GetFiles(_testRoot, "*.tmp"));
    }

    [Fact]
    public async Task PushedWrite_WriterFails_PublishesNoNewFile()
    {
        await Assert.ThrowsAsync<IOException>(() => _sut.WriteAsync("absent.bin", WritesThenFails(9, 9)));

        Assert.False(File.Exists(Path.Combine(_testRoot, "absent.bin")));
        Assert.Empty(Directory.GetFiles(_testRoot, "*.tmp"));
    }

    [Fact]
    public async Task PushedWrite_NoContent_CreatesAnEmptyFile()
    {
        await _sut.WriteAsync("empty.bin", (_, _) => Task.CompletedTask);

        Assert.Equal(0, new FileInfo(Path.Combine(_testRoot, "empty.bin")).Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Overwrite_KeepsTheUnixModeOfTheReplacedFile(bool pushed)
    {
        if (OperatingSystem.IsWindows())
            return;
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        var fullPath = Path.Combine(_testRoot, "mode.bin");
        await File.WriteAllBytesAsync(fullPath, [1]);
        File.SetUnixFileMode(fullPath, mode);

        if (pushed)
            await _sut.WriteAsync("mode.bin", Writes(9));
        else
            await _sut.WriteAsync("mode.bin", new MemoryStream([9]));

        Assert.Equal(mode, File.GetUnixFileMode(fullPath));
    }

    [Fact]
    public async Task DeleteAbandonedUploads_RemovesOnlyOldTemporaryUploadsOfThatFolder()
    {
        var cutoff = DateTime.UtcNow.AddDays(-1);
        var directory = Path.Combine(_testRoot, "sub");
        var nested = Path.Combine(directory, "nested");
        Directory.CreateDirectory(nested);
        string Old(string path)
        {
            File.WriteAllBytes(path, [1]);
            File.SetLastWriteTimeUtc(path, cutoff.AddHours(-1));
            return path;
        }
        var abandoned = Old(Path.Combine(directory, ".kaimo-upload-old.tmp"));
        var userFile = Old(Path.Combine(directory, "old.tmp"));
        var otherFolder = Old(Path.Combine(nested, ".kaimo-upload-old.tmp"));
        var active = Path.Combine(directory, ".kaimo-upload-active.tmp");
        File.WriteAllBytes(active, [1]);

        await _sut.DeleteAbandonedUploadsAsync("sub", cutoff);

        Assert.False(File.Exists(abandoned));
        Assert.True(File.Exists(userFile));
        Assert.True(File.Exists(otherFolder));
        Assert.True(File.Exists(active));
    }

    [Fact]
    public async Task DeleteAbandonedUploads_MissingFolder_DoesNothing()
    {
        await _sut.DeleteAbandonedUploadsAsync("does-not-exist", DateTime.UtcNow);
    }

    [Fact]
    public async Task PushedWrite_PathTraversal_ThrowsAndNeverRunsTheWriter()
    {
        bool ran = false;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _sut.WriteAsync("../../evil.bin", (_, _) =>
        {
            ran = true;
            return Task.CompletedTask;
        }));

        Assert.False(ran);
    }
}
