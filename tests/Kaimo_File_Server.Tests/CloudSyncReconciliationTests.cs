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
public sealed class CloudSyncReconciliationTests
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

    /// <summary>Dictionary-backed remote endpoint implementing the sync contract.</summary>
    private sealed class InMemoryRemote : ICloudConnection
    {
        private readonly Dictionary<string, (byte[] Data, DateTime Modified)> _files = new();
        private readonly HashSet<string> _dirs = new();
        private readonly Dictionary<string, DateTime> _dirTimes = new();

        /// <summary>Folder times the sync engine stamped, by normalized path.</summary>
        public Dictionary<string, DateTime> DirStamps { get; } = new();

        public string ServiceName => "in-memory";
        public Task Dispose() => Task.CompletedTask;

        // Test hooks for the resilience path: uploads to a path in HardFailUploads
        // always throw a non-transient error; TransientUploads throws the given
        // number of transient errors before succeeding (to exercise the retry).
        public HashSet<string> HardFailUploads { get; } = new();
        public Dictionary<string, int> TransientUploads { get; } = new();

        public void PutFile(string path, string content, DateTime modified)
            => _files[Normalize(path)] = (System.Text.Encoding.UTF8.GetBytes(content), modified);

        public void PutDir(string path, DateTime? modified = null)
        {
            _dirs.Add(Normalize(path));
            if (modified is { } time)
                _dirTimes[Normalize(path)] = time;
        }

        public void RemoveFile(string path) => _files.Remove(Normalize(path));

        public bool HasFile(string path) => _files.ContainsKey(Normalize(path));

        public string ReadText(string path)
            => System.Text.Encoding.UTF8.GetString(_files[Normalize(path)].Data);

        public Task TrySetDirectoryModifiedTimeAsync(string path, DateTime modifiedUtc, CancellationToken ct = default)
        {
            DirStamps[Normalize(path)] = modifiedUtc;
            return Task.CompletedTask;
        }

        public Task UploadAsync(string path, Stream data, DateTime modifiedTime, CancellationToken ct = default)
        {
            string key = Normalize(path);
            if (HardFailUploads.Contains(key))
                throw new UnauthorizedAccessException($"upload denied for {key}");
            if (TransientUploads.TryGetValue(key, out int remaining) && remaining > 0)
            {
                TransientUploads[key] = remaining - 1;
                throw new IOException($"transient failure for {key}");
            }
            using var ms = new MemoryStream();
            data.CopyTo(ms);
            _files[key] = (ms.ToArray(), modifiedTime);
            return Task.CompletedTask;
        }

        public async Task DownloadAsync(string path, Stream target, CancellationToken ct = default)
            => await target.WriteAsync(_files[Normalize(path)].Data, ct);

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        {
            _dirs.Add(Normalize(path));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
        {
            string key = Normalize(path);
            if (isDirectory)
            {
                _dirs.RemoveWhere(d => d == key || d.StartsWith(key + "/", StringComparison.Ordinal));
                foreach (var file in _files.Keys
                             .Where(f => f.StartsWith(key + "/", StringComparison.Ordinal)).ToList())
                    _files.Remove(file);
            }
            else
            {
                _files.Remove(key);
            }
            return Task.CompletedTask;
        }

        public Task<long> GetDirectorySizeAsync(string path, CancellationToken ct = default)
        {
            string key = Normalize(path);
            long total = _files
                .Where(f => key.Length == 0 || f.Key.StartsWith(key + "/", StringComparison.Ordinal))
                .Sum(f => (long)f.Value.Data.Length);
            return Task.FromResult(total);
        }

        /// <summary>Counts how often each directory is listed, to prove reconciliation reuses the pre-scan.</summary>
        public Dictionary<string, int> ListCalls { get; } = new();

        public Task<IReadOnlyList<CloudItemMeta>> ListAsync(string path, CancellationToken ct = default)
        {
            string key = Normalize(path);
            ListCalls[key] = ListCalls.TryGetValue(key, out int n) ? n + 1 : 1;
            var items = new List<CloudItemMeta>();
            foreach (var (file, value) in _files.Where(f => Parent(f.Key) == key))
                items.Add(new CloudItemMeta(false, NameOf(file), "/" + file, value.Data.Length, value.Modified));
            foreach (var dir in _dirs.Where(d => Parent(d) == key))
                items.Add(new CloudItemMeta(true, NameOf(dir), "/" + dir, 0,
                    _dirTimes.TryGetValue(dir, out var time) ? time : DateTime.UnixEpoch));
            return Task.FromResult<IReadOnlyList<CloudItemMeta>>(items);
        }
    }

    /// <summary>Minimal in-memory <see cref="IFileService"/> covering only the members the sync engine calls.</summary>
    private sealed class InMemoryFileService : IFileService
    {
        private readonly Dictionary<string, (byte[] Data, DateTime Modified)> _files = new();
        private readonly HashSet<string> _dirs = new();
        private readonly Dictionary<string, DateTime> _dirTimes = new();

        public List<(string Path, bool Recycle)> Deletions { get; } = [];

        /// <summary>Folder times the sync engine stamped, by normalized path.</summary>
        public Dictionary<string, DateTime> DirStamps { get; } = new();

        /// <summary>Last-access times set on pulled files.</summary>
        public Dictionary<string, DateTime> Accessed { get; } = new();

        /// <summary>Paths read with <c>preserveAccessTime</c>.</summary>
        public List<string> PreservedReads { get; } = [];

        /// <summary>Ordered "write:path" / "stamp:path" events, to assert post-order stamping.</summary>
        public List<string> Log { get; } = [];

        // Invoked after each successful listing; lets a test delete a folder
        // between its listing and the processing of its children (mid-run delete).
        public Action<string>? AfterList { get; set; }

        public void PutFile(string path, string content, DateTime modified)
            => _files[Normalize(path)] = (System.Text.Encoding.UTF8.GetBytes(content), modified);

        public void PutDir(string path, DateTime? modified = null)
        {
            _dirs.Add(Normalize(path));
            if (modified is { } time)
                _dirTimes[Normalize(path)] = time;
        }

        public void RemoveDir(string path)
        {
            string key = Normalize(path);
            _dirs.RemoveWhere(d => d == key || d.StartsWith(key + "/", StringComparison.Ordinal));
            foreach (var file in _files.Keys
                         .Where(f => f.StartsWith(key + "/", StringComparison.Ordinal)).ToList())
                _files.Remove(file);
        }

        public bool DirExists(string path)
        {
            string key = Normalize(path);
            return key.Length == 0 || _dirs.Contains(key)
                || _files.Keys.Any(f => f.StartsWith(key + "/", StringComparison.Ordinal));
        }

        public void RemoveFile(string path) => _files.Remove(Normalize(path));

        public bool HasFile(string path) => _files.ContainsKey(Normalize(path));

        public string ReadText(string path)
            => System.Text.Encoding.UTF8.GetString(_files[Normalize(path)].Data);

        public DateTime GetModified(string path) => _files[Normalize(path)].Modified;

        public Task<List<FileMetadata>> ListAsync(string directoryPath, UserContext user)
        {
            string key = Normalize(directoryPath);
            if (!DirExists(key))
                throw new DirectoryNotFoundException($"Directory '{key}' does not exist.");
            var items = new List<FileMetadata>();
            foreach (var (file, value) in _files.Where(f => Parent(f.Key) == key))
                items.Add(new FileMetadata
                {
                    Name = NameOf(file), Path = file, IsDirectory = false,
                    Size = value.Data.Length, ModifiedAt = value.Modified
                });
            foreach (var dir in _dirs.Where(d => Parent(d) == key))
                items.Add(new FileMetadata
                {
                    Name = NameOf(dir), Path = dir, IsDirectory = true,
                    ModifiedAt = _dirTimes.TryGetValue(dir, out var time) ? time : DateTime.UnixEpoch
                });
            AfterList?.Invoke(key);
            return Task.FromResult(items);
        }

        public Task<Stream> ReadFileAsync(string path, UserContext user)
            => _files.TryGetValue(Normalize(path), out var entry)
                ? Task.FromResult<Stream>(new MemoryStream(entry.Data))
                : throw new FileNotFoundException($"'{path}' not found");

        public Task<Stream> ReadFileAsync(string path, UserContext user, bool preserveAccessTime)
        {
            if (preserveAccessTime)
                PreservedReads.Add(Normalize(path));
            return ReadFileAsync(path, user);
        }

        public Task<FileMetadata> GetMetadataAsync(string path, UserContext user)
            => Task.FromResult(new FileMetadata { Path = Normalize(path), IsDirectory = DirExists(path) });

        public Task WriteFileAsync(string path, Stream data, UserContext user, CancellationToken ct = default)
            => WriteFileAsync(path, data, user, modifiedAtUtc: null, ct);

        public Task WriteFileAsync(string path, Stream data, UserContext user, DateTime? modifiedAtUtc, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            data.CopyTo(ms);
            _files[Normalize(path)] = (ms.ToArray(), modifiedAtUtc ?? DateTime.UnixEpoch);
            return Task.CompletedTask;
        }

        public Task CreateDirectoryAsync(string path, UserContext user)
        {
            _dirs.Add(Normalize(path));
            return Task.CompletedTask;
        }

        public Task SetModifiedAtAsync(string path, UserContext user, DateTime time)
        {
            string key = Normalize(path);
            if (_files.TryGetValue(key, out var entry))
                _files[key] = (entry.Data, time);
            else if (DirExists(key))
            {
                DirStamps[key] = time;
                Log.Add("stamp:" + key);
            }
            return Task.CompletedTask;
        }

        public Task DeleteFileAsync(string path, UserContext user, bool isRecycleEnabled)
        {
            string key = Normalize(path);
            Deletions.Add((key, isRecycleEnabled));
            _files.Remove(key);
            _dirs.RemoveWhere(d => d == key || d.StartsWith(key + "/", StringComparison.Ordinal));
            foreach (var file in _files.Keys
                         .Where(f => f.StartsWith(key + "/", StringComparison.Ordinal)).ToList())
                _files.Remove(file);
            return Task.CompletedTask;
        }

        public Task<long> GetDirectorySizeAsync(string relativePath, UserContext user)
        {
            string key = Normalize(relativePath);
            long total = _files
                .Where(f => key.Length == 0 || f.Key.StartsWith(key + "/", StringComparison.Ordinal))
                .Sum(f => (long)f.Value.Data.Length);
            return Task.FromResult(total);
        }

        // ---- Members not exercised by the reconciliation engine ----
        public Task<bool> CanReadAsync(string path, UserContext user) => throw new NotSupportedException();
        public Task<bool> CanWriteAsync(string path, UserContext user) => throw new NotSupportedException();
        public Task<bool> CanCreateAsync(string path, UserContext user) => throw new NotSupportedException();
        public Task<bool> CanDeleteAsync(string path, UserContext user) => throw new NotSupportedException();
        public Task<bool> CanListAsync(string path, UserContext user) => throw new NotSupportedException();
        public string ToAbsolutePath(string path) => throw new NotSupportedException();
        public Task CreateFileAsync(string path, UserContext user) => throw new NotSupportedException();
        public Task RenameAsync(string oldPath, string newPath, UserContext user) => throw new NotSupportedException();
        public Task UnzipAsync(string zipPath, string targetPath, UserContext user) => throw new NotSupportedException();
        public Task ArchiveAsync(List<string> sourcePaths, string targetPath, string format, UserContext user) => throw new NotSupportedException();
        public Task OnFileCreated(string absolutePath, Task<Stream> fileData) => throw new NotSupportedException();
        public Task onDirectoryCreated(string absolutePath) => throw new NotSupportedException();
        public Task onFileDeleted(string absolutePath) => throw new NotSupportedException();
        public Task onDirectoryDeleted(string absolutePath) => throw new NotSupportedException();
        public Task NotifyExternalCloseAsync(string path, UserContext user, Func<Task<Stream>> openCapturedContent) => throw new NotSupportedException();
        public Task NotifyExternalMkdirAsync(string path, UserContext user) => throw new NotSupportedException();
        public Task NotifyExternalDeleteAsync(string path, bool isDirectory) => throw new NotSupportedException();
        public Task NotifyExternalRenameAsync(string oldPath, string newPath, bool isDirectory, Guid sambaLifecycleEventId) => throw new NotSupportedException();
        public Task<HashSet<string>> FilterReadablePathsAsync(IReadOnlyList<(string relativePath, bool isDirectory)> items, UserContext user) => throw new NotSupportedException();
        public Task<FileOpenResult> OpenAsync(string path, OpenMode mode, AccessIntent intent, ShareIntent share, UserContext user, CancellationToken ct = default)
        {
            // The pull path opens a write handle and streams the download into it.
            // Commit happens on session dispose, mirroring FileSession semantics.
            var session = new InMemorySession(this, Normalize(path), user);
            return Task.FromResult(new FileOpenResult(session, FileOpenStatus.Created));
        }

        /// <summary>Buffers offset-based writes and commits them to the parent store on dispose.</summary>
        private sealed class InMemorySession(
            InMemoryFileService owner,
            string key,
            UserContext user) : IFileSession
        {
            private readonly MemoryStream _buffer = new();
            private DateTime _modified = DateTime.UnixEpoch;

            public string RelativePath => key;
            public string AbsolutePath => key;
            public bool IsDirectory => false;
            public long Length => _buffer.Length;
            public UserContext User => user;
            public bool IsReadOnly => false;

            public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default)
            {
                _buffer.Position = offset;
                _buffer.Write(data.Span);
                return ValueTask.CompletedTask;
            }

            public ValueTask SetTimesAsync(FileTimes times, CancellationToken ct = default)
            {
                if (times.LastWritten is { } written)
                    _modified = written;
                if (times.LastAccessed is { } accessed)
                    owner.Accessed[key] = accessed;
                return ValueTask.CompletedTask;
            }

            public ValueTask SetLengthAsync(long length, CancellationToken ct = default)
            {
                _buffer.SetLength(length);
                return ValueTask.CompletedTask;
            }

            public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

            public ValueTask DisposeAsync()
            {
                owner._files[key] = (_buffer.ToArray(), _modified);
                owner.Log.Add("write:" + key);
                return ValueTask.CompletedTask;
            }

            public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
                => throw new NotSupportedException();
            public ValueTask RenameAsync(string newRelativePath, bool replaceExisting, CancellationToken ct = default)
                => throw new NotSupportedException();
            public void MarkDeleteOnClose() => throw new NotSupportedException();
        }
        public Task<IFileSession> OpenSnapshotAsync(string realPath, DateTime snapshotTimestampUtc, UserContext user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<DateTime>> GetSnapshotTimestampsAsync(UserContext user) => throw new NotSupportedException();
        public Task<List<FileVersion>> GetFileVersionsAsync(string path, UserContext user) => throw new NotSupportedException();
        public Task<Stream> ReadFileVersionAsync(string path, DateTime snapshotTimestampUtc, UserContext user) => throw new NotSupportedException();
        public Task RestoreFileVersionAsync(string path, DateTime snapshotTimestampUtc, UserContext user) => throw new NotSupportedException();
        public Task<List<DateTime>> GetFolderSnapshotTimestampsAsync(string folderPath, UserContext user) => throw new NotSupportedException();
        public Task<List<FileVersion>> GetFolderSnapshotAsync(string folderPath, DateTime asOfUtc, UserContext user) => throw new NotSupportedException();
    }
}
