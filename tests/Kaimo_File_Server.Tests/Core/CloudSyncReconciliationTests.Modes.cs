using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Infrastructure.Clouds;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Per-mode reconciliation rules (pull mirror, push, newer-copy direction), root and
/// sub-folder listing failures and the share bookkeeping guard.
/// </summary>
public sealed partial class CloudSyncReconciliationTests
{
    [Fact]
    public async Task Pull_WithDeleteSync_RemovesLocalOnlyItem_ThroughTheRecycleBin()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/keep.txt", "keep", Time(10));
        var local = new InMemoryFileService();
        local.PutFile("keep.txt", "keep", Time(10));
        local.PutFile("extra.txt", "local only", Time(10));
        var options = new CloudSyncTransferOptions(
            new CloudSyncAdvancedSettings { SyncDeletions = true }, honorRecycleBin: true);

        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.Pull, options, previousManifest: null);

        Assert.True(local.HasFile("keep.txt"));
        Assert.False(local.HasFile("extra.txt"));
        Assert.Equal([("extra.txt", true)], local.Deletions);
        Assert.False(remote.HasFile("/extra.txt"));
    }

    [Fact]
    public async Task Pull_WithoutDeleteSync_LeavesLocalOnlyItem_AndDoesNotUploadIt()
    {
        var remote = new InMemoryRemote();
        var local = new InMemoryFileService();
        local.PutFile("extra.txt", "local only", Time(10));
        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings());

        var manifest = await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.Pull, options, previousManifest: null);

        Assert.True(local.HasFile("extra.txt"));
        Assert.Empty(local.Deletions);
        Assert.False(remote.HasFile("/extra.txt"));
        Assert.DoesNotContain("/extra.txt", manifest!.Paths);
    }

    [Fact]
    public async Task Push_IgnoresRemoteOnlyItems()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/remote.txt", "remote only", Time(10));
        var local = new InMemoryFileService();
        var options = new CloudSyncTransferOptions(
            new CloudSyncAdvancedSettings { SyncDeletions = true });

        var manifest = await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.Push, options, previousManifest: null);

        Assert.False(local.HasFile("remote.txt"));
        Assert.True(remote.HasFile("/remote.txt"));
        Assert.DoesNotContain("/remote.txt", manifest!.Paths);
    }

    [Theory]
    [InlineData(20, 10, "local")]   // local newer: pushed
    [InlineData(10, 20, "remote")]  // remote newer: push never pulls
    public async Task Push_TransfersOnlyANewerLocalFile(int localMinute, int remoteMinute, string expectedRemote)
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote", Time(remoteMinute));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "local", Time(localMinute));
        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings());

        var manifest = await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.Push, options, previousManifest: null);

        Assert.Equal(expectedRemote, remote.ReadText("/a.txt"));
        Assert.Equal("local", local.ReadText("a.txt"));
        Assert.Contains("/a.txt", manifest!.Paths);
    }

    [Theory]
    [InlineData(10, 20, "remote")]  // remote newer: pulled
    [InlineData(20, 10, "local")]   // local newer: pull never pushes
    public async Task Pull_TransfersOnlyANewerRemoteFile(int localMinute, int remoteMinute, string expectedLocal)
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote", Time(remoteMinute));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "local", Time(localMinute));
        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings());

        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.Pull, options, previousManifest: null);

        Assert.Equal(expectedLocal, local.ReadText("a.txt"));
        Assert.Equal("remote", remote.ReadText("/a.txt"));
    }

    [Fact]
    public async Task TwoWay_WithDeleteSync_LocalFolderDeletion_RemovesTheRemoteFolder()
    {
        var remote = new InMemoryRemote();
        var local = new InMemoryFileService();
        local.PutDir("docs");
        local.PutFile("docs/a.txt", "content", Time(10));
        var options = new CloudSyncTransferOptions(
            new CloudSyncAdvancedSettings { SyncDeletions = true });

        var baseline = await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay, options, previousManifest: null);
        Assert.True(remote.HasFile("/docs/a.txt"));

        local.RemoveDir("docs");
        var next = await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay, options, previousManifest: baseline);

        Assert.False(remote.HasFile("/docs/a.txt"));
        Assert.DoesNotContain("/docs", next!.Paths);
        Assert.False(local.DirExists("docs"));
    }

    [Fact]
    public async Task Sync_MissingLocalRoot_ThrowsSyncDirectoryMissing_ForTheLocalSide()
    {
        var remote = new InMemoryRemote();
        var local = new InMemoryFileService();
        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings());

        var error = await Assert.ThrowsAsync<SyncDirectoryMissingException>(() =>
            ((ICloudConnection)remote).SyncAsync(
                local, User, RemoteRoot, "missing", SyncMode.Push, options, previousManifest: null));

        Assert.Equal(SyncEndpoint.Local, error.Endpoint);
    }

    [Fact]
    public async Task Sync_UnreadableSubFolder_IsRecordedAsListFailure_AndTheRestStillSyncs()
    {
        var remote = new InMemoryRemote();
        remote.PutDir("/locked");
        var local = new InMemoryFileService();
        local.PutDir("locked");
        local.PutFile("a.txt", "content", Time(10));
        local.AfterList = key =>
        {
            if (key == "locked")
                throw new UnauthorizedAccessException("denied");
        };
        var failures = new List<SyncFailure>();
        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings());

        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay, options, previousManifest: null,
            reportProgress: null, failures: failures);

        var failure = Assert.Single(failures);
        Assert.Equal(SyncFailureOperation.List, failure.Operation);
        Assert.EndsWith("locked", failure.Path);
        Assert.True(remote.HasFile("/a.txt"));
    }

    [Fact]
    public async Task Push_SkipsTheRecycleBin()
    {
        var remote = new InMemoryRemote();
        var local = new InMemoryFileService();
        local.PutDir(".RECYCLE_BIN");
        local.PutFile(".RECYCLE_BIN/old.txt", "recycled", Time(10));
        local.PutFile("a.txt", "content", Time(10));
        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings());

        var manifest = await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.Push, options, previousManifest: null);

        Assert.True(remote.HasFile("/a.txt"));
        Assert.False(remote.HasFile("/.RECYCLE_BIN/old.txt"));
        Assert.DoesNotContain(manifest!.Paths, p => p.Contains(".RECYCLE_BIN"));
    }

    [Fact]
    public async Task Sync_ReportsProgress_EndingAtOneHundredPercent()
    {
        var remote = new InMemoryRemote();
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "content", Time(10));
        var reports = new List<(string? Text, int Percent)>();
        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings());

        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.Push, options, previousManifest: null,
            reportProgress: (text, percent) => reports.Add((text, percent)));

        Assert.Equal("Scanning...", reports[0].Text);
        Assert.Equal(100, reports[^1].Percent);
        Assert.Contains(reports, r => r.Text == "Pushing a.txt...");
    }
}
