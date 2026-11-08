using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.DataServices;
using Xunit;

namespace Kaimo_File_Server.Core.Tests.Services.DataServices;

/// <summary>
/// A folder link on the remote server that points back to one of its own ancestors (a symbolic
/// link loop on a Samba or WebDAV server) must not be walked until the server's path length
/// limit: the sync would list the same content over and over and copy it once per repetition.
/// </summary>
public sealed partial class CloudSyncReconciliationTests
{
    // /a holds f.txt and b; /a/b holds g.txt and "up", a link back to /a.
    private static InMemoryRemote LoopTree()
    {
        var remote = new InMemoryRemote();
        remote.PutDir("/a");
        remote.PutFile("/a/f.txt", "f", Time(10));
        remote.PutDir("/a/b");
        remote.PutFile("/a/b/g.txt", "g", Time(10));
        remote.PutLink("/a/b/up", "/a");
        return remote;
    }

    private static void AssertLoopSkipped(InMemoryFileService local, List<SyncFailure> failures)
    {
        Assert.Equal("f", local.ReadText("a/f.txt"));
        Assert.Equal("g", local.ReadText("a/b/g.txt"));
        Assert.False(local.DirExists("a/b/up"));
        // Reported, so a folder left out of the sync is visible to the administrator.
        var failure = Assert.Single(failures);
        Assert.Equal(SyncFailureOperation.List, failure.Operation);
        Assert.Equal("/a/b/up", failure.Path);
    }

    [Fact]
    public async Task PreScan_FolderLinkToAnAncestor_IsNotFollowed()
    {
        var remote = LoopTree();
        var local = new InMemoryFileService();
        var failures = new List<SyncFailure>();

        await PullTreeAsync(remote, local, failures);

        AssertLoopSkipped(local, failures);
        // The link itself is listed once (that is how the repetition shows), nothing below it.
        Assert.DoesNotContain(remote.ListCalls.Keys, key => key.StartsWith("a/b/up/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TreeListing_FolderLinkToAnAncestor_IsNotFollowed()
    {
        var remote = LoopTree();
        remote.SupportsTreeListing = true;
        var local = new InMemoryFileService();
        var failures = new List<SyncFailure>();

        await PullTreeAsync(remote, local, failures);

        AssertLoopSkipped(local, failures);
        Assert.Empty(remote.ListCalls);
    }

    [Fact]
    public async Task TwoWay_LocalFolderNamedLikeALoop_IsNeitherPushedIntoItNorDeleted()
    {
        // Left behind by a run before this guard: pushing it would write into the link target.
        var remote = LoopTree();
        var local = new InMemoryFileService();
        local.PutDir("a");
        local.PutDir("a/b");
        local.PutDir("a/b/up");
        local.PutFile("a/b/up/old.txt", "old", Time(10));
        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings { SyncDeletions = true });
        var previous = new SyncManifest();
        previous.Paths.UnionWith(["/a", "/a/b", "/a/b/up", "/a/b/up/old.txt"]);

        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay, options, previous);

        Assert.True(local.HasFile("a/b/up/old.txt"));
        Assert.False(remote.HasFile("/a/b/up/old.txt"));
        Assert.Equal("g", local.ReadText("a/b/g.txt"));
    }

    [Fact]
    public async Task FolderLinkToASibling_IsSynced()
    {
        // Not a loop: the content only appears twice, so it is synced like any folder.
        var remote = new InMemoryRemote();
        remote.PutDir("/x");
        remote.PutFile("/x/f.txt", "f", Time(10));
        remote.PutFile("/x/g.txt", "g", Time(10));
        remote.PutDir("/a");
        remote.PutLink("/a/lnk", "/x");
        var local = new InMemoryFileService();
        var failures = new List<SyncFailure>();

        await PullTreeAsync(remote, local, failures);

        Assert.Empty(failures);
        Assert.Equal("f", local.ReadText("a/lnk/f.txt"));
        Assert.Equal("g", local.ReadText("x/g.txt"));
    }

    [Fact]
    public async Task NestedFoldersWithTheSameSingleEntry_AreSynced()
    {
        // "p/p/p" made in one go lists the same single entry on every level (same name, same
        // second); that alone must not be mistaken for a loop.
        var remote = new InMemoryRemote();
        remote.PutDir("/p");
        remote.PutDir("/p/p");
        remote.PutDir("/p/p/p");
        remote.PutDir("/p/p/p/p");
        remote.PutFile("/p/p/p/p/deep.txt", "deep", Time(10));
        var local = new InMemoryFileService();
        var failures = new List<SyncFailure>();

        await PullTreeAsync(remote, local, failures);

        Assert.Empty(failures);
        Assert.Equal("deep", local.ReadText("p/p/p/p/deep.txt"));
    }
}
