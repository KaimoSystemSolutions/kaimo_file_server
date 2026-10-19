using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Clouds;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Exercises the two-way reconciliation algorithm (a default method on
/// <see cref="ICloudConnection"/>) end to end against in-memory local and remote
/// stores, focusing on the delete-propagation and recycle-bin behavior.
/// </summary>
public sealed partial class CloudSyncReconciliationTests
{
    private static readonly UserContext User =
        new(new User(Guid.NewGuid(), "Tester", "tester", "hash", "nt"), [], [], []);

    private const string LocalRoot = "";
    private const string RemoteRoot = "/";

    [Fact]
    public async Task TwoWay_WithoutDeleteSync_RestoresLocallyDeletedFile()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "content", Time(10));
        var local = new InMemoryFileService(); // empty: simulates a local deletion

        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings
        {
            SyncDeletions = false
        });

        var manifest = await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay, options,
            previousManifest: null);

        // No delete sync: the file is pulled back rather than removed remotely.
        Assert.True(local.HasFile("a.txt"));
        Assert.True(remote.HasFile("/a.txt"));
        // A baseline manifest is still produced so enabling delete sync later
        // takes effect immediately rather than wasting a run.
        Assert.NotNull(manifest);
        Assert.Contains("/a.txt", manifest!.Paths);
    }

    [Fact]
    public async Task TwoWay_WithDeleteSync_PropagatesLocalDeletionToRemote()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "content", Time(10));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "content", Time(10));

        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings
        {
            SyncDeletions = true
        });

        // First run establishes the baseline manifest.
        var baseline = await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay, options,
            previousManifest: null);
        Assert.NotNull(baseline);
        Assert.Contains("/a.txt", baseline!.Paths);

        // The file is deleted locally; the next run must remove it remotely.
        local.RemoveFile("a.txt");
        var next = await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay, options,
            previousManifest: baseline);

        Assert.False(remote.HasFile("/a.txt"));
        Assert.DoesNotContain("/a.txt", next!.Paths);
    }

    [Fact]
    public async Task TwoWay_WithDeleteSync_RemoteDeletionRemovedLocally_HonorsRecycleBin()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "content", Time(10));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "content", Time(10));

        var options = new CloudSyncTransferOptions(
            new CloudSyncAdvancedSettings { SyncDeletions = true },
            honorRecycleBin: true);

        var baseline = await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay, options,
            previousManifest: null);

        // The file is deleted remotely; the next run must remove it locally,
        // routed through the recycle bin.
        remote.RemoveFile("/a.txt");
        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay, options,
            previousManifest: baseline);

        Assert.False(local.HasFile("a.txt"));
        Assert.Contains(("a.txt", true), local.Deletions);
    }

    [Fact]
    public async Task TwoWay_WithDeleteSync_FirstRunCopiesNewFile_NeverDeletes()
    {
        var remote = new InMemoryRemote(); // empty
        var local = new InMemoryFileService();
        local.PutFile("new.txt", "hello", Time(10));

        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings
        {
            SyncDeletions = true
        });

        // No baseline manifest: a one-sided item must be treated as new.
        var manifest = await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay, options,
            previousManifest: null);

        Assert.True(remote.HasFile("/new.txt"));
        Assert.True(local.HasFile("new.txt"));
        Assert.Contains("/new.txt", manifest!.Paths);
        Assert.Empty(local.Deletions);
    }

    [Fact]
    public async Task TwoWay_BaselineFromRunWithoutDeleteSync_MakesEnableEffectiveImmediately()
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", "content", Time(10));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "content", Time(10));

        // Run once with delete sync OFF: it must still record a baseline manifest.
        var offOptions = new CloudSyncTransferOptions(
            new CloudSyncAdvancedSettings { SyncDeletions = false });
        var baseline = await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay, offOptions,
            previousManifest: null);
        Assert.NotNull(baseline);

        // Now enable delete sync and delete locally. Using the pre-existing
        // baseline, the very first delete-sync run must propagate the deletion.
        local.RemoveFile("a.txt");
        var onOptions = new CloudSyncTransferOptions(
            new CloudSyncAdvancedSettings { SyncDeletions = true });
        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay, onOptions,
            previousManifest: baseline);

        Assert.False(remote.HasFile("/a.txt"));
    }

    [Fact]
    public async Task Sync_OneFileFails_OthersStillTransfer_AndFailureIsCollected()
    {
        var remote = new InMemoryRemote(); // empty: both files are new local uploads
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "aaa", Time(10));
        local.PutFile("b.txt", "bbb", Time(10));
        remote.HardFailUploads.Add("a.txt"); // definitive (non-transient) failure

        var failures = new List<SyncFailure>();
        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings());

        var manifest = await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay, options,
            previousManifest: null, reportProgress: null, failures: failures);

        // The good file is transferred despite the other failing.
        Assert.True(remote.HasFile("/b.txt"));
        Assert.False(remote.HasFile("/a.txt"));
        // Exactly the failing file is reported, tagged as an upload failure.
        var failure = Assert.Single(failures);
        Assert.EndsWith("a.txt", failure.Path);
        Assert.Equal(SyncFailureOperation.Upload, failure.Operation);
        // A file that failed to upload must not be recorded as converged, or a
        // later run could treat it as remotely deleted and remove it locally.
        Assert.Contains("/b.txt", manifest!.Paths);
        Assert.DoesNotContain("/a.txt", manifest.Paths);
    }

    [Fact]
    public async Task Sync_TransientUploadFailure_IsRetriedAndSucceeds()
    {
        var remote = new InMemoryRemote();
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "aaa", Time(10));
        remote.TransientUploads["a.txt"] = 2; // fail twice, succeed on the third attempt

        var failures = new List<SyncFailure>();
        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings());

        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay, options,
            previousManifest: null, reportProgress: null, failures: failures);

        Assert.True(remote.HasFile("/a.txt"));
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Sync_AlreadyCancelled_ThrowsAndRecordsNoFailure()
    {
        var remote = new InMemoryRemote();
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "aaa", Time(10));

        var failures = new List<SyncFailure>();
        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ((ICloudConnection)remote).SyncAsync(
                local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay, options,
                previousManifest: null, reportProgress: null, failures: failures,
                cancellationToken: cts.Token));

        // A genuine cancellation is never bucketed as a per-item failure.
        Assert.Empty(failures);
    }

    [Fact]
    public async Task TwoWay_FolderDeletedMidRun_IsNotResurrected_AndDeletionReachesRemoteNextRun()
    {
        var remote = new InMemoryRemote();
        remote.PutDir("/backup");
        remote.PutDir("/backup/run1");
        remote.PutFile("/backup/pull.txt", "remote", Time(20)); // remote newer: would pull
        remote.PutFile("/backup/push.txt", "remote", Time(10)); // local newer: would push
        remote.PutFile("/backup/remote-only.txt", "r", Time(10));
        remote.PutDir("/backup/remote-only-dir");
        var local = new InMemoryFileService();
        local.PutDir("backup");
        local.PutDir("backup/run1");
        local.PutFile("backup/pull.txt", "local", Time(10));
        local.PutFile("backup/push.txt", "local", Time(20));
        local.PutFile("backup/local-only.txt", "l", Time(10));
        // The folder is listed, then deleted while the run walks its children.
        local.AfterList = dir => { if (dir == "backup") local.RemoveDir("backup"); };

        var failures = new List<SyncFailure>();
        var options = new CloudSyncTransferOptions(new CloudSyncAdvancedSettings { SyncDeletions = true });

        var manifest = await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay, options,
            previousManifest: null, reportProgress: null, failures: failures);

        // No per-item noise, and pulls/folder creates did not resurrect the folder.
        Assert.Empty(failures);
        Assert.False(local.DirExists("backup"));
        Assert.False(remote.HasFile("/backup/local-only.txt"));
        Assert.Contains("/backup", manifest!.Paths);

        local.AfterList = null;
        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay, options,
            previousManifest: manifest, reportProgress: null, failures: failures);

        // The next run propagates the local deletion to the remote.
        Assert.Empty(failures);
        Assert.False(remote.HasFile("/backup/pull.txt"));
        Assert.False(remote.HasFile("/backup/remote-only.txt"));
        Assert.False(local.DirExists("backup"));
    }

    [Theory]
    [InlineData(800, "bbbb", false)]   // second-precision provider rounding: same file
    [InlineData(-800, "bbbb", false)]
    [InlineData(3000, "bbbb", true)]   // a real change
    [InlineData(-3000, "bbbb", true)]
    [InlineData(800, "bbbbbb", true)]  // inside the window, but the size proves a change
    [InlineData(-800, "bbbbbb", true)]
    public async Task TwoWay_ModifiedTimeTolerance_IgnoresProviderRounding(
        int remoteOffsetMs, string remoteContent, bool transfers)
    {
        var remote = new InMemoryRemote();
        remote.PutFile("/a.txt", remoteContent, Time(10).AddMilliseconds(remoteOffsetMs));
        var local = new InMemoryFileService();
        local.PutFile("a.txt", "aaaa", Time(10));

        await ((ICloudConnection)remote).SyncAsync(
            local, User, RemoteRoot, LocalRoot, SyncMode.TwoWay,
            new CloudSyncTransferOptions(new CloudSyncAdvancedSettings()), previousManifest: null);

        // Whichever direction ran, both sides end with the same content.
        Assert.Equal(transfers, local.ReadText("a.txt") == remote.ReadText("/a.txt"));
    }

    private static DateTime Time(int minute) =>
        new(2026, 1, 1, 0, minute, 0, DateTimeKind.Utc);

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private static string Parent(string normalized)
    {
        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }

    private static string NameOf(string normalized)
    {
        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? normalized : normalized[(slash + 1)..];
    }
}
