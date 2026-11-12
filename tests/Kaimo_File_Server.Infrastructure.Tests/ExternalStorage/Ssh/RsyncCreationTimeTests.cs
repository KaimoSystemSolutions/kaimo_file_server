using Kaimo_File_Server.Infrastructure.Storage;
using Xunit;
using Kaimo_File_Server.Infrastructure.ExternalStorage.Ssh;

namespace Kaimo_File_Server.Infrastructure.Tests.ExternalStorage.Ssh;

/// <summary>
/// rsync preserves modification times (<c>--archive</c>) but Linux rsync cannot keep creation times,
/// so every file and folder it creates would show the time of the pull as created. Kaimo stamps the
/// modification time as creation time on what rsync created; an updated file is left alone, since
/// its mtime is the latest edit, not the time the file came into being.
/// </summary>
public sealed class RsyncCreationTimeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kaimo-rsync-btime-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // GetFullPath normalizes '/' to the platform separator (Windows: '\'), like the production code does.
    private string Expected(string relative) => Path.GetFullPath(Path.Combine(_root, relative));

    [Fact]
    public void CreatedEntryAbsolutePath_ReturnsNewFilesAndNewFolders()
    {
        Assert.Equal(Expected("docs/new file.txt"),
            RsyncProcessRunner.CreatedEntryAbsolutePath(_root, ">f+++++++++ docs/new file.txt"));
        Assert.Equal(Expected("docs"),
            RsyncProcessRunner.CreatedEntryAbsolutePath(_root, "cd+++++++++ docs/"));
    }

    [Theory]
    [InlineData(">f.st...... report.pdf")]    // updated file: its mtime is the latest edit
    [InlineData(">f..t...... touched.txt")]
    [InlineData(".d..t...... docs/")]         // existing folder, only its time changed
    [InlineData("*deleting   gone.txt")]
    [InlineData("<f+++++++++ pushed.txt")]
    [InlineData(".f...p..... perms-only.txt")]
    [InlineData("cd+++++++++ ../escape/")]
    [InlineData(">f+++++++++ ../escape.txt")]
    public void CreatedEntryAbsolutePath_SkipsEverythingElse(string line)
        => Assert.Null(RsyncProcessRunner.CreatedEntryAbsolutePath(_root, line));

    [Fact]
    public void StampCreationFromModified_UsesTheModifiedTimeOfFilesAndFolders()
    {
        var mtime = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        string file = Expected("a.txt");
        string dir = Expected("dir");
        File.WriteAllText(file, "x");
        Directory.CreateDirectory(dir);
        File.SetLastWriteTimeUtc(file, mtime);
        Directory.SetLastWriteTimeUtc(dir, mtime);

        RsyncProcessRunner.StampCreationFromModified([file, dir, Expected("vanished.txt")]);

        Assert.Equal(mtime, FileBirthTime.GetUtc(new FileInfo(file)));
        Assert.Equal(mtime, FileBirthTime.GetUtc(new DirectoryInfo(dir)));
        // Only the creation time changes.
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(file));
        Assert.Equal(mtime, Directory.GetLastWriteTimeUtc(dir));
    }

    [Fact]
    public async Task RunThenStampCreatedAsync_AfterASuccessfulRun_StampsWhatWasCreated()
    {
        var mtime = new DateTime(2002, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        string file = CreateFile("ok.txt", mtime);

        await RsyncProcessRunner.RunThenStampCreatedAsync(() => Task.CompletedTask, [file]);

        Assert.Equal(mtime, FileBirthTime.GetUtc(new FileInfo(file)));
    }

    public static TheoryData<Exception> FailedRuns => new()
    {
        new IOException("The rsync helper failed with exit code 23."), // partial transfer
        new OperationCanceledException()
    };

    [Theory]
    [MemberData(nameof(FailedRuns))]
    public async Task RunThenStampCreatedAsync_AfterAFailedRun_StillStampsWhatWasCreated(Exception failure)
    {
        // The next run no longer itemizes these files as new, so this is the only chance to stamp them.
        var mtime = new DateTime(2003, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        string file = CreateFile("partial.txt", mtime);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => RsyncProcessRunner.RunThenStampCreatedAsync(() => throw failure, [file]));

        Assert.Same(failure, thrown);
        Assert.Equal(mtime, FileBirthTime.GetUtc(new FileInfo(file)));
    }

    [Fact]
    public async Task RunThenStampCreatedAsync_ForAPush_StampsNothing()
    {
        bool ran = false;

        await RsyncProcessRunner.RunThenStampCreatedAsync(() => { ran = true; return Task.CompletedTask; }, null);

        Assert.True(ran);
    }

    private string CreateFile(string name, DateTime mtime)
    {
        string file = Expected(name);
        File.WriteAllText(file, "x");
        File.SetLastWriteTimeUtc(file, mtime);
        return file;
    }
}
