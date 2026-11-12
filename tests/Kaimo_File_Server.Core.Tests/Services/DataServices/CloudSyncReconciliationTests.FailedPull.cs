using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.CloudSync;
using Xunit;

namespace Kaimo_File_Server.Core.Tests.Services.DataServices;

/// <summary>
/// A pull that fails must not touch the local file. A truncated copy would carry the time of the
/// failed run, so it would count as the newer side: a two-way sync would push it over the intact
/// remote file, and a pull would never repair it.
/// </summary>
public sealed partial class CloudSyncReconciliationTests
{
    private const int Always = 1000;

    private static async Task<List<SyncFailure>> SyncCollectingFailuresAsync(
        InMemoryRemote remote, InMemoryFileService local, SyncMode mode)
    {
        var failures = new List<SyncFailure>();
        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, mode,
            new CloudSyncTransferOptions(new CloudSyncAdvancedSettings()), previousManifest: null,
            failures: failures);
        return failures;
    }

    [Fact]
    public async Task Pull_DownloadFails_KeepsTheExistingLocalFileUntouched()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote-content", Time(20));
        remote.FailingDownloads["a.txt"] = Always;
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "stale", Time(10));

        var failures = await SyncCollectingFailuresAsync(remote, local, SyncMode.Pull);

        Assert.Equal(SyncFailureOperation.Download, Assert.Single(failures).Operation);
        Assert.Equal("stale", local.ReadText("a.txt"));
        Assert.Equal(Time(10), local.GetModified("a.txt"));
    }

    [Fact]
    public async Task Pull_DownloadFails_CreatesNoLocalFile()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote-content", Time(20));
        remote.FailingDownloads["a.txt"] = Always;
        var local = new InMemoryFileService();

        var failures = await SyncCollectingFailuresAsync(remote, local, SyncMode.Pull);

        Assert.Single(failures);
        Assert.False(local.HasFile("a.txt"));
    }

    [Fact]
    public async Task Pull_AfterAFailedRun_NextRunDeliversTheFile()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote-content", Time(20));
        remote.FailingDownloads["a.txt"] = Always;
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "stale", Time(10));
        await SyncCollectingFailuresAsync(remote, local, SyncMode.Pull);
        remote.FailingDownloads.Clear();

        var failures = await SyncCollectingFailuresAsync(remote, local, SyncMode.Pull);

        Assert.Empty(failures);
        Assert.Equal("remote-content", local.ReadText("a.txt"));
        Assert.Equal(Time(20), local.GetModified("a.txt"));
    }

    [Fact]
    public async Task TwoWay_AfterAFailedPull_NothingIsPushedOverTheIntactRemoteFile()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote-content", Time(20));
        remote.FailingDownloads["a.txt"] = Always;
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "stale", Time(10));
        await SyncCollectingFailuresAsync(remote, local, SyncMode.TwoWay);
        remote.FailingDownloads.Clear();

        await SyncCollectingFailuresAsync(remote, local, SyncMode.TwoWay);

        Assert.Equal("remote-content", remote.ReadText("/a.txt"));
        Assert.Equal("remote-content", local.ReadText("a.txt"));
    }

    [Fact]
    public async Task Pull_TransientDownloadFailure_IsRetriedAndDeliversTheCompleteFile()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote-content", Time(20));
        remote.FailingDownloads["a.txt"] = 1;
        var local = new InMemoryFileService();

        var failures = await SyncCollectingFailuresAsync(remote, local, SyncMode.Pull);

        Assert.Empty(failures);
        Assert.Equal("remote-content", local.ReadText("a.txt"));
    }
}
