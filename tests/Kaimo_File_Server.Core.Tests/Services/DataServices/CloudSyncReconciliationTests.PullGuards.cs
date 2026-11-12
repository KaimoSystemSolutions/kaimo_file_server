using Kaimo_File_Server.Core.Services.CloudSync;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Xunit;

namespace Kaimo_File_Server.Core.Tests.Services.DataServices;

/// <summary>
/// What a pull must respect beyond the transfer itself: local changes made while it runs,
/// failures that retrying cannot fix, and temporary upload files.
/// </summary>
public sealed partial class CloudSyncReconciliationTests
{
    private const string TemporaryUpload = "sub/.kaimo-upload-0123456789abcdef.tmp";

    [Theory]
    [InlineData(SyncMode.Pull)]
    [InlineData(SyncMode.TwoWay)]
    public async Task Pull_LocalFileEditedDuringTheDownload_KeepsTheEdit(SyncMode mode)
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote-content", Time(20));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "stale", Time(10));
        remote.DuringDownload = () => local.PutFile("a.txt", "edited", Time(15));

        var failures = await SyncCollectingFailuresAsync(remote, local, mode);

        Assert.Empty(failures);
        Assert.Equal("edited", local.ReadText("a.txt"));
        Assert.Equal(Time(15), local.GetModified("a.txt"));
    }

    [Fact]
    public async Task Pull_LocalFileDeletedDuringTheDownload_IsNotRecreated()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote-content", Time(20));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "stale", Time(10));
        remote.DuringDownload = () => local.RemoveFile("a.txt");

        var failures = await SyncCollectingFailuresAsync(remote, local, SyncMode.TwoWay);

        Assert.Empty(failures);
        Assert.False(local.HasFile("a.txt"));
    }

    [Fact]
    public async Task Pull_LocalFileCreatedDuringTheDownload_IsNotOverwritten()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote-content", Time(20));
        var local = new InMemoryFileService();
        remote.DuringDownload = () => local.PutFile("a.txt", "created-here", Time(25));

        var failures = await SyncCollectingFailuresAsync(remote, local, SyncMode.Pull);

        Assert.Empty(failures);
        Assert.Equal("created-here", local.ReadText("a.txt"));
    }

    [Fact]
    public async Task Pull_LocalFileEditedDuringTheDownload_DownloadsOnlyOnce()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote-content", Time(20));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "stale", Time(10));
        remote.DuringDownload = () => local.PutFile("a.txt", "edited", Time(15));

        await SyncCollectingFailuresAsync(remote, local, SyncMode.Pull);

        Assert.Equal(1, remote.DownloadAttempts);
    }

    [Fact]
    public async Task Pull_LocalPoolFull_IsNotRetried()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote-content", Time(20));
        remote.DownloadError = new IOException("No space left on device") { HResult = 28 };
        var local = new InMemoryFileService();

        var failures = await SyncCollectingFailuresAsync(remote, local, SyncMode.Pull);

        Assert.Single(failures);
        Assert.Equal(1, remote.DownloadAttempts);
    }

    [Fact]
    public async Task Pull_ItemRejectedByTheRemote_IsNotRetried()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote-content", Time(20));
        remote.DownloadError = new RemoteItemRejectedException("NT_STATUS_OBJECT_NAME_NOT_FOUND");
        var local = new InMemoryFileService();

        var failures = await SyncCollectingFailuresAsync(remote, local, SyncMode.Pull);

        Assert.Single(failures);
        Assert.Equal(1, remote.DownloadAttempts);
    }

    [Fact]
    public async Task Pull_OtherIOFailure_IsStillRetried()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote-content", Time(20));
        remote.DownloadError = new IOException("connection reset");
        var local = new InMemoryFileService();

        await SyncCollectingFailuresAsync(remote, local, SyncMode.Pull);

        Assert.Equal(3, remote.DownloadAttempts);
    }

    [Theory]
    [InlineData(SyncMode.Push)]
    [InlineData(SyncMode.TwoWay)]
    public async Task Sync_LeftoverTemporaryUpload_IsNeverPushed(SyncMode mode)
    {
        var remote = new InMemoryRemote();
        remote.PutDir("/sub");
        var local = new InMemoryFileService();
        local.PutDir("sub");
        local.PutFile(TemporaryUpload, "partial", Time(10));

        var failures = await SyncCollectingFailuresAsync(remote, local, mode);

        Assert.Empty(failures);
        Assert.False(remote.HasFile("/" + TemporaryUpload));
    }

    [Fact]
    public async Task Sync_CleansEveryVisitedLocalFolderOfAbandonedUploads()
    {
        var remote = new InMemoryRemote();
        remote.PutDir("/sub");
        var local = new InMemoryFileService();
        local.PutDir("sub");

        await SyncCollectingFailuresAsync(remote, local, SyncMode.TwoWay);

        Assert.Contains(LocalRoot.Trim('/'), local.AbandonedUploadSweeps);
        Assert.Contains(local.AbandonedUploadSweeps, folder => folder.EndsWith("sub", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sync_FailedCleanup_DoesNotFailTheRun()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote-content", Time(20));
        var local = new InMemoryFileService { AbandonedUploadSweepError = new UnauthorizedAccessException("no write access") };

        var failures = await SyncCollectingFailuresAsync(remote, local, SyncMode.Pull);

        Assert.Empty(failures);
        Assert.Equal("remote-content", local.ReadText("a.txt"));
    }
}
