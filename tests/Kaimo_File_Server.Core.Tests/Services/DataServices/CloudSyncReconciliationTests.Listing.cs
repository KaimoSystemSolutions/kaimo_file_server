using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.CloudSync;
using Xunit;

namespace Kaimo_File_Server.Core.Tests.Services.DataServices;

/// <summary>
/// Remote pre-scan speed and robustness: parallel per-directory listings up to the provider's
/// limit, one whole-tree listing where the provider offers it, retries of transient listing
/// faults, and a broken subfolder that must not abort the run.
/// </summary>
public sealed partial class CloudSyncReconciliationTests
{
    private static InMemoryRemote WideTree(int folders)
    {
        var remote = new InMemoryRemote();
        for (int i = 0; i < folders; i++)
        {
            remote.PutDir($"/d{i}");
            remote.PutFile($"/d{i}/f.txt", $"content {i}", Time(10));
        }
        return remote;
    }

    private static Task<SyncManifest?> PullTreeAsync(
        InMemoryRemote remote, InMemoryFileService local, List<SyncFailure>? failures = null)
        => ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.Pull,
            new CloudSyncTransferOptions(new CloudSyncAdvancedSettings()),
            previousManifest: null, failures: failures);

    [Fact]
    public async Task PreScan_ListsFoldersInParallel_UpToTheProviderLimit()
    {
        var remote = WideTree(8);
        remote.ListConcurrency = 3;
        remote.ListDelay = TimeSpan.FromMilliseconds(60);
        var local = new InMemoryFileService();

        await PullTreeAsync(remote, local);

        Assert.Equal(3, remote.MaxListsInFlight);
        for (int i = 0; i < 8; i++)
            Assert.Equal($"content {i}", local.ReadText($"d{i}/f.txt"));
        // Reconciliation still reuses the parallel pre-scan instead of listing again.
        Assert.All(remote.ListCalls.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task PreScan_ProviderWithoutLimit_ListsOneFolderAtATime()
    {
        var remote = WideTree(4);
        remote.ListDelay = TimeSpan.FromMilliseconds(20);

        await PullTreeAsync(remote, new InMemoryFileService());

        Assert.Equal(1, remote.MaxListsInFlight);
    }

    [Fact]
    public async Task PreScan_ParallelNestedTree_PullsEveryFile()
    {
        var remote = new InMemoryRemote { ListConcurrency = 4 };
        remote.PutDir("/a");
        remote.PutDir("/a/b");
        remote.PutDir("/a/b/c");
        remote.PutDir("/x");
        remote.PutFile("/root.txt", "r", Time(10));
        remote.PutFile("/a/b/c/deep.txt", "deep", Time(10));
        remote.PutFile("/x/y.txt", "y", Time(10));
        var local = new InMemoryFileService();

        var manifest = await PullTreeAsync(remote, local);

        Assert.Equal("deep", local.ReadText("a/b/c/deep.txt"));
        Assert.Equal("y", local.ReadText("x/y.txt"));
        Assert.Equal("r", local.ReadText("root.txt"));
        Assert.Contains("/a/b/c/deep.txt", manifest!.Paths);
    }

    [Fact]
    public async Task Listing_TransientFault_IsRetried()
    {
        var remote = WideTree(1);
        remote.TransientLists["d0"] = 1;
        var local = new InMemoryFileService();
        var failures = new List<SyncFailure>();

        await PullTreeAsync(remote, local, failures);

        Assert.Empty(failures);
        Assert.Equal("content 0", local.ReadText("d0/f.txt"));
    }

    [Fact]
    public async Task Listing_SubfolderDenied_IsRecordedAndTheRestIsSynced()
    {
        var remote = WideTree(2);
        remote.HardFailLists.Add("d0");
        var local = new InMemoryFileService();
        var failures = new List<SyncFailure>();

        await PullTreeAsync(remote, local, failures);

        Assert.Equal("content 1", local.ReadText("d1/f.txt"));
        Assert.False(local.HasFile("d0/f.txt"));
        var failure = Assert.Single(failures);
        Assert.Equal(SyncFailureOperation.List, failure.Operation);
        Assert.Equal("/d0", failure.Path);
    }

    [Fact]
    public async Task Listing_RootDenied_StillFailsTheRun()
    {
        var remote = WideTree(1);
        remote.HardFailLists.Add("");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => PullTreeAsync(remote, new InMemoryFileService(), []));
    }

    [Fact]
    public async Task TreeListing_WhenOffered_ReplacesPerFolderListings()
    {
        var remote = WideTree(3);
        remote.PutDir("/d0/sub");
        remote.PutFile("/d0/sub/s.txt", "s", Time(10));
        remote.SupportsTreeListing = true;
        var local = new InMemoryFileService();

        await PullTreeAsync(remote, local);

        Assert.Equal(1, remote.TreeListCalls);
        Assert.Empty(remote.ListCalls);
        Assert.Equal("s", local.ReadText("d0/sub/s.txt"));
        Assert.Equal("content 2", local.ReadText("d2/f.txt"));
    }

    [Fact]
    public async Task TreeListing_Failure_FallsBackToPerFolderListings()
    {
        var remote = WideTree(2);
        remote.SupportsTreeListing = true;
        remote.TreeListingThrows = true;
        var local = new InMemoryFileService();
        var failures = new List<SyncFailure>();

        await PullTreeAsync(remote, local, failures);

        Assert.Empty(failures);
        Assert.Equal("content 1", local.ReadText("d1/f.txt"));
        Assert.NotEmpty(remote.ListCalls);
    }

    [Fact]
    public async Task TreeListing_MissingFolder_IsListedLive()
    {
        var remote = WideTree(2);
        remote.SupportsTreeListing = true;
        remote.TreeListingOmits.Add("d1");
        var local = new InMemoryFileService();

        await PullTreeAsync(remote, local);

        Assert.Equal("content 1", local.ReadText("d1/f.txt"));
        Assert.Equal(1, remote.ListCalls["d1"]);
    }
}
