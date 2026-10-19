using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Clouds;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed partial class CloudSyncReconciliationTests
{
    [Fact]
    public async Task Pull_NewerRemoteFile_StreamsBytesAndModifiedTimeLocally()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote-content", Time(20));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "stale", Time(10)); // older: must be overwritten

        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings());

        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.Pull, options,
            previousManifest: null);

        // The download streamed straight through the write session: content and
        // the source modification time both land locally.
        Assert.Equal("remote-content", local.ReadText("a.txt"));
        Assert.Equal(Time(20), local.GetModified("a.txt"));
        // The copy was not opened since its last change: atime = source mtime, not the sync time.
        Assert.Equal(Time(20), local.Accessed["a.txt"]);
    }

    [Fact]
    public async Task Push_ReadsTheSourceWithoutCountingAnAccess()
    {
        var remote = new InMemoryRemote();
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "aaa", Time(10));

        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.Push,
            new CloudSyncTransferOptions(new CloudSyncAdvancedSettings()), previousManifest: null);

        Assert.Contains("a.txt", local.PreservedReads);
    }

    [Fact]
    public async Task Push_RecordsUploadedItemsInManifest()
    {
        // The file browser compares against it to flag local-only items as pending upload.
        var remote = new InMemoryRemote();
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "aaa", Time(10));

        var manifest = await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.Push,
            new CloudSyncTransferOptions(new CloudSyncAdvancedSettings()), previousManifest: null);

        Assert.NotNull(manifest);
        Assert.Contains("/a.txt", manifest!.Paths);
    }

    [Fact]
    public async Task Pull_NewRemoteFolder_GetsTheRemoteTimeAfterItsContent()
    {
        var remote = new InMemoryRemote();
        remote.PutDir("/sub", Time(5));
        remote.PutDir("/sub/deep", Time(3));
        remote.PutFile("/sub/a.txt", "a", Time(10));
        remote.PutFile("/sub/deep/b.txt", "b", Time(10));
        var local = new InMemoryFileService();

        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.Pull,
            new CloudSyncTransferOptions(new CloudSyncAdvancedSettings()), previousManifest: null);

        Assert.Equal(Time(5), local.DirStamps["sub"]);
        Assert.Equal(Time(3), local.DirStamps["sub/deep"]);
        // Post-order: writing a child would move the folder time again, so the stamp comes last.
        Assert.True(local.Log.IndexOf("stamp:sub") > local.Log.IndexOf("write:sub/a.txt"));
        Assert.True(local.Log.IndexOf("stamp:sub") > local.Log.IndexOf("stamp:sub/deep"));
    }

    [Fact]
    public async Task Pull_ExistingFolderWithNothingPulled_IsNotStamped()
    {
        var remote = new InMemoryRemote();
        remote.PutDir("/sub", Time(5));
        remote.PutFile("/sub/a.txt", "a", Time(10));
        var local = new InMemoryFileService();
        local.PutDir("sub", Time(7));
        local.PutFile("sub/a.txt", "a", Time(10));

        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.Pull,
            new CloudSyncTransferOptions(new CloudSyncAdvancedSettings()), previousManifest: null);

        Assert.Empty(local.DirStamps);
    }

    [Fact]
    public async Task Push_NewLocalFolder_GetsTheLocalTimeRemotely()
    {
        var remote = new InMemoryRemote();
        var local = new InMemoryFileService();
        local.PutDir("sub", Time(5));
        local.PutFile("sub/a.txt", "a", Time(10));

        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.Push,
            new CloudSyncTransferOptions(new CloudSyncAdvancedSettings()), previousManifest: null);

        Assert.True(remote.HasFile("/sub/a.txt"));
        Assert.Equal(Time(5), remote.DirStamps["sub"]);
    }

    [Fact]
    public async Task TwoWay_FolderChangedOnBothSides_KeepsItsTimes()
    {
        var remote = new InMemoryRemote();
        remote.PutDir("/d", Time(5));
        remote.PutFile("/d/remote.txt", "r", Time(10));
        var local = new InMemoryFileService();
        local.PutDir("d", Time(6));
        local.PutFile("d/local.txt", "l", Time(10));

        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay,
            new CloudSyncTransferOptions(new CloudSyncAdvancedSettings()), previousManifest: null);

        // Both sides gained a file: neither folder time is a valid source for the other.
        Assert.True(local.HasFile("d/remote.txt"));
        Assert.True(remote.HasFile("/d/local.txt"));
        Assert.Empty(local.DirStamps);
        Assert.Empty(remote.DirStamps);
    }

    [Fact]
    public async Task Pull_ProviderWithoutFolderTimes_DoesNotStampTheFolder()
    {
        var remote = new InMemoryRemote();
        remote.PutDir("/sub", DateTime.MinValue); // Dropbox lists folders without a date
        remote.PutFile("/sub/a.txt", "a", Time(10));
        var local = new InMemoryFileService();

        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.Pull,
            new CloudSyncTransferOptions(new CloudSyncAdvancedSettings()), previousManifest: null);

        Assert.True(local.HasFile("sub/a.txt"));
        Assert.Empty(local.DirStamps);
    }

    [Fact]
    public async Task Pull_ListsEachRemoteDirectoryOnce()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/root.txt", "r", Time(10));
        remote.PutDir("/sub");
        remote.PutFile("/sub/child.txt", "c", Time(10));
        var local = new InMemoryFileService();

        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings());

        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.Pull, options,
            previousManifest: null);

        // The size pre-scan's listings are reused by reconciliation: each remote
        // directory is fetched exactly once (was twice before caching).
        Assert.All(remote.ListCalls.Values, count => Assert.Equal(1, count));
        Assert.True(local.HasFile("sub/child.txt"));
    }
}
