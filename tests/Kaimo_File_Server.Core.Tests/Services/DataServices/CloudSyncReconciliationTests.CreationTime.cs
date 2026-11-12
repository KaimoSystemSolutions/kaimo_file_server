using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.CloudSync;
using Xunit;

namespace Kaimo_File_Server.Core.Tests.Services.DataServices;

/// <summary>
/// Creation times of pulled items: the remote creation time when the provider reports one,
/// otherwise the remote modification time, never the time of the sync run.
/// </summary>
public sealed partial class CloudSyncReconciliationTests
{
    private static Task PullAsync(InMemoryRemote remote, InMemoryFileService local)
        => ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.Pull,
            new CloudSyncTransferOptions(new CloudSyncAdvancedSettings()), previousManifest: null);

    [Fact]
    public async Task Pull_NewFile_WithoutRemoteCreationTime_UsesTheModifiedTimeAsCreationTime()
    {
        // smbclient, SFTP and rsync listings carry no creation time.
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote", Time(20));
        var local = new InMemoryFileService();

        await PullAsync(remote, local);

        Assert.Equal(Time(20), local.Created["a.txt"]);
        Assert.Equal(Time(20), local.GetModified("a.txt"));
        Assert.Equal(Time(20), local.Accessed["a.txt"]);
    }

    [Fact]
    public async Task Pull_NewFile_WithRemoteCreationTime_KeepsTheRemoteCreationTime()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote", Time(20));
        remote.SetCreated("/a.txt", Time(5));
        var local = new InMemoryFileService();

        await PullAsync(remote, local);

        Assert.Equal(Time(5), local.Created["a.txt"]);
        Assert.Equal(Time(20), local.GetModified("a.txt"));
    }

    [Fact]
    public async Task Pull_OverwrittenFile_WithoutRemoteCreationTime_KeepsTheLocalCreationTime()
    {
        // An update changes the content, not the identity: the modified time is no creation time.
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote", Time(20));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "stale", Time(10));

        await PullAsync(remote, local);

        Assert.Equal("remote", local.ReadText("a.txt"));
        Assert.DoesNotContain("a.txt", local.Created.Keys);
    }

    [Fact]
    public async Task Pull_OverwrittenFile_WithRemoteCreationTime_AdoptsTheRemoteCreationTime()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote", Time(20));
        remote.SetCreated("/a.txt", Time(5));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "stale", Time(10));

        await PullAsync(remote, local);

        Assert.Equal(Time(5), local.Created["a.txt"]);
    }

    [Fact]
    public async Task Pull_OverwrittenFile_WithEarlierRemoteCreationTime_AdoptsTheRemoteCreationTime()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote", Time(20));
        remote.SetCreated("/a.txt", Time(5));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "stale", Time(10));
        local.Created["a.txt"] = Time(8);

        await PullAsync(remote, local);

        Assert.Equal(Time(5), local.Created["a.txt"]);
    }

    [Fact]
    public async Task Pull_OverwrittenFile_WithLaterRemoteCreationTime_KeepsTheLocalCreationTime()
    {
        // Google Drive keeps the first-upload time as createdTime, and OneDrive reports the upload
        // time for items pushed without one: neither may replace the older local creation time.
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "remote", Time(20));
        remote.SetCreated("/a.txt", Time(15));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "stale", Time(10));
        local.Created["a.txt"] = Time(3);

        await PullAsync(remote, local);

        Assert.Equal("remote", local.ReadText("a.txt"));
        Assert.Equal(Time(3), local.Created["a.txt"]);
    }

    [Fact]
    public async Task Pull_NewFolder_WithoutRemoteCreationTime_UsesTheModifiedTimeAsCreationTime()
    {
        var remote = new InMemoryRemote();
        remote.PutDir("/sub", Time(5));
        remote.PutDir("/sub/deep", Time(3));
        remote.PutFile("/sub/deep/b.txt", "b", Time(10));
        var local = new InMemoryFileService();

        await PullAsync(remote, local);

        Assert.Equal(Time(5), local.Created["sub"]);
        Assert.Equal(Time(3), local.Created["sub/deep"]);
        Assert.Equal(Time(5), local.DirStamps["sub"]);
        Assert.Equal(Time(3), local.DirStamps["sub/deep"]);
    }

    [Fact]
    public async Task Pull_NewFolder_WithRemoteCreationTime_KeepsTheRemoteCreationTime()
    {
        var remote = new InMemoryRemote();
        remote.PutDir("/sub", Time(5));
        remote.SetCreated("/sub", Time(2));
        remote.PutFile("/sub/a.txt", "a", Time(10));
        var local = new InMemoryFileService();

        await PullAsync(remote, local);

        Assert.Equal(Time(2), local.Created["sub"]);
        Assert.Equal(Time(5), local.DirStamps["sub"]);
    }

    [Fact]
    public async Task Pull_ExistingFolder_KeepsItsCreationTime()
    {
        var remote = new InMemoryRemote();
        remote.PutDir("/sub", Time(5));
        remote.PutFile("/sub/a.txt", "a", Time(20));
        var local = new InMemoryFileService();
        local.PutDir("sub", Time(7));
        local.PutFile("sub/a.txt", "a", Time(10));

        await PullAsync(remote, local);

        Assert.DoesNotContain("sub", local.Created.Keys);
    }

    [Fact]
    public async Task Pull_NewFolder_FromProviderWithoutFolderTimes_SetsNoCreationTime()
    {
        var remote = new InMemoryRemote();
        remote.PutDir("/sub", DateTime.MinValue); // Dropbox lists folders without a date
        remote.PutFile("/sub/a.txt", "a", Time(10));
        var local = new InMemoryFileService();

        await PullAsync(remote, local);

        Assert.DoesNotContain("sub", local.Created.Keys);
    }
}
