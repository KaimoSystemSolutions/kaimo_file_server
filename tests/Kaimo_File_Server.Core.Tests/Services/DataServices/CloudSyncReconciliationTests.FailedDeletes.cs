using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.CloudSync;
using Xunit;

namespace Kaimo_File_Server.Core.Tests.Services.DataServices;

/// <summary>
/// A deletion that fails (no delete permission, a file held open) must stay pending: the path
/// remains in the manifest so the next run retries the delete. Dropping it would make the
/// surviving copy look new, and the next run would copy the deleted file back.
/// </summary>
public sealed partial class CloudSyncReconciliationTests
{
    private static readonly CloudSyncTransferOptions DeleteSync =
        new(new CloudSyncAdvancedSettings { SyncDeletions = true });

    private static async Task<SyncManifest?> TwoWayAsync(
        InMemoryRemote remote, InMemoryFileService local, SyncManifest? previous)
        => await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay, DeleteSync,
            previousManifest: previous, reportProgress: null, failures: new List<SyncFailure>());

    private static (InMemoryRemote Remote, InMemoryFileService Local) SyncedPair()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "content", Time(10));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "content", Time(10));
        return (remote, local);
    }

    [Fact]
    public async Task TwoWay_FailedRemoteDelete_StaysPending_AndIsRetriedNextRun()
    {
        var (remote, local) = SyncedPair();
        var baseline = await TwoWayAsync(remote, local, previous: null);

        local.RemoveFile("a.txt");
        remote.HardFailDeletes.Add("a.txt");
        var failedRun = await TwoWayAsync(remote, local, baseline);

        Assert.Contains("/a.txt", failedRun!.Paths);
        Assert.False(local.HasFile("a.txt"));

        remote.HardFailDeletes.Clear();
        await TwoWayAsync(remote, local, failedRun);

        Assert.False(remote.HasFile("/a.txt"));
        Assert.False(local.HasFile("a.txt"));
    }

    [Fact]
    public async Task TwoWay_FailedLocalDelete_StaysPending_AndIsRetriedNextRun()
    {
        var (remote, local) = SyncedPair();
        var baseline = await TwoWayAsync(remote, local, previous: null);

        remote.RemoveFile("/a.txt");
        local.HardFailDeletes.Add("a.txt");
        var failedRun = await TwoWayAsync(remote, local, baseline);

        Assert.Contains("/a.txt", failedRun!.Paths);
        Assert.False(remote.HasFile("/a.txt"));

        local.HardFailDeletes.Clear();
        await TwoWayAsync(remote, local, failedRun);

        Assert.False(local.HasFile("a.txt"));
        Assert.False(remote.HasFile("/a.txt"));
    }
}
