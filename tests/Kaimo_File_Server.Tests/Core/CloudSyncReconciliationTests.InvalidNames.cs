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
    // ─────────────────── Windows-invalid remote names ───────────────────

    [Fact]
    public async Task Pull_InvalidRemoteNames_AreStoredRenamed()
    {
        var remote = new InMemoryRemote();
        remote.PutDir("/x:y");
        remote.PutFile("/x:y/f?.txt", "inner", Time(10));
        remote.PutFile("/CON.txt", "con", Time(10));
        var local = new InMemoryFileService();
        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings());

        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.Pull, options, previousManifest: null);

        Assert.Equal("inner", local.ReadText("x_y/f_.txt"));
        Assert.Equal("con", local.ReadText("CON_.txt"));
    }

    [Theory]
    [InlineData(SyncMode.Pull)]
    [InlineData(SyncMode.TwoWay)]
    public async Task Sync_RenamedPull_IsStableOnTheNextRun(SyncMode mode)
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a:b.txt", "content", Time(10));
        var local = new InMemoryFileService();
        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings { SyncDeletions = true });

        var first = await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, mode, options, previousManifest: null);
        var failures = new List<SyncFailure>();
        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, mode, options, previousManifest: first,
            reportProgress: null, failures: failures);

        // The local copy is matched to its remote original: not deleted, not pushed back as a twin.
        Assert.True(local.HasFile("a_b.txt"));
        Assert.True(remote.HasFile("/a:b.txt"));
        Assert.False(remote.HasFile("/a_b.txt"));
        Assert.Empty(failures);
    }

    [Fact]
    public async Task TwoWay_LocalChangeToRenamedFile_UpdatesTheRemoteOriginal()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a:b.txt", "old", Time(10));
        var local = new InMemoryFileService();
        local.PutFile("a_b.txt", "new", Time(20));
        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings());

        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay, options, previousManifest: null);

        Assert.Equal("new", remote.ReadText("/a:b.txt"));
        Assert.False(remote.HasFile("/a_b.txt"));
    }

    [Fact]
    public async Task Pull_RemoteNamesCollidingAfterRenaming_KeepTheExactMatch_AndReportTheOther()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a:b.txt", "renamed", Time(10));
        remote.PutFile("/a_b.txt", "exact", Time(10));
        var local = new InMemoryFileService();
        var failures = new List<SyncFailure>();
        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings());

        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.Pull, options, previousManifest: null,
            reportProgress: null, failures: failures);

        Assert.Equal("exact", local.ReadText("a_b.txt"));
        var failure = Assert.Single(failures);
        Assert.EndsWith("a:b.txt", failure.Path);
        Assert.Equal(SyncFailureOperation.Download, failure.Operation);
    }
}
