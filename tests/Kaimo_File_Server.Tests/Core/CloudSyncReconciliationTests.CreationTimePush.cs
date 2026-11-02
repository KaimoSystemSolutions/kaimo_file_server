using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Infrastructure.Clouds;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Creation times of pushed items: the local creation time travels with the upload; a new
/// item without one falls back to its modification time, an overwrite then keeps the remote one.
/// </summary>
public sealed partial class CloudSyncReconciliationTests
{
    private static Task SyncAsync(InMemoryRemote remote, InMemoryFileService local, SyncMode mode)
        => ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, mode,
            new CloudSyncTransferOptions(new CloudSyncAdvancedSettings()), previousManifest: null);

    [Fact]
    public async Task Push_NewFile_SendsTheLocalCreationTime()
    {
        var remote = new InMemoryRemote();
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "local", Time(20));
        local.Created["a.txt"] = Time(4);

        await SyncAsync(remote, local, SyncMode.Push);

        Assert.Equal(Time(4), remote.SentCreated["a.txt"]);
    }

    [Fact]
    public async Task Push_NewFile_WithoutLocalCreationTime_SendsTheModifiedTime()
    {
        var remote = new InMemoryRemote();
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "local", Time(20));

        await SyncAsync(remote, local, SyncMode.Push);

        Assert.Equal(Time(20), remote.SentCreated["a.txt"]);
    }

    [Fact]
    public async Task Push_OverwrittenFile_SendsTheLocalCreationTime()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "stale", Time(10));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "local", Time(20));
        local.Created["a.txt"] = Time(4);

        await SyncAsync(remote, local, SyncMode.Push);

        Assert.Equal("local", remote.ReadText("/a.txt"));
        Assert.Equal(Time(4), remote.SentCreated["a.txt"]);
    }

    [Fact]
    public async Task Push_OverwrittenFile_WithEarlierRemoteCreationTime_KeepsTheRemoteCreationTime()
    {
        // A local copy pulled before creation times were synced shows the pull time as created.
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "stale", Time(10));
        remote.SetCreated("/a.txt", Time(2));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "local", Time(20));
        local.Created["a.txt"] = Time(9);

        await SyncAsync(remote, local, SyncMode.Push);

        Assert.Equal("local", remote.ReadText("/a.txt"));
        Assert.Null(remote.SentCreated["a.txt"]);
    }

    [Fact]
    public async Task Push_OverwrittenFile_WithLaterRemoteCreationTime_SendsTheLocalCreationTime()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "stale", Time(10));
        remote.SetCreated("/a.txt", Time(9));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "local", Time(20));
        local.Created["a.txt"] = Time(4);

        await SyncAsync(remote, local, SyncMode.Push);

        Assert.Equal(Time(4), remote.SentCreated["a.txt"]);
    }

    [Fact]
    public async Task Push_OverwrittenFile_WithoutLocalCreationTime_KeepsTheRemoteCreationTime()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "stale", Time(10));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "local", Time(20));

        await SyncAsync(remote, local, SyncMode.Push);

        Assert.Null(remote.SentCreated["a.txt"]);
    }

    [Fact]
    public async Task Push_NewFolderTree_SendsTheLocalCreationTimes()
    {
        var remote = new InMemoryRemote();
        var local = new InMemoryFileService();
        local.PutDir("sub", Time(5));
        local.Created["sub"] = Time(1);
        local.PutDir("sub/deep", Time(6));
        local.PutFile("sub/deep/b.txt", "b", Time(10));
        local.Created["sub/deep/b.txt"] = Time(2);

        await SyncAsync(remote, local, SyncMode.Push);

        Assert.Equal(Time(1), remote.SentCreated["sub"]);
        Assert.Equal(Time(6), remote.SentCreated["sub/deep"]); // no creation time known: mtime
        Assert.Equal(Time(2), remote.SentCreated["sub/deep/b.txt"]);
        Assert.Equal(Time(5), remote.DirStamps["sub"]);
    }

    [Fact]
    public async Task TwoWay_CarriesCreationTimesInBothDirections()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/from-remote.txt", "r", Time(30));
        remote.SetCreated("/from-remote.txt", Time(3));
        var local = new InMemoryFileService();
        local.PutFile("from-local.txt", "l", Time(40));
        local.Created["from-local.txt"] = Time(7);

        await SyncAsync(remote, local, SyncMode.TwoWay);

        Assert.Equal(Time(3), local.Created["from-remote.txt"]);
        Assert.Equal(Time(7), remote.SentCreated["from-local.txt"]);
    }
}
