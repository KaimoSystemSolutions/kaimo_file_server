using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Services.CloudSync;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Xunit;

namespace Kaimo_File_Server.Core.Tests.Services.DataServices;

public sealed partial class CloudSyncReconciliationTests
{
    /// <summary>Dictionary-backed remote endpoint implementing the sync contract.</summary>
    private sealed class InMemoryRemote : ICloudConnection
    {
        private readonly Dictionary<string, (byte[] Data, DateTime Modified)> _files = new();
        private readonly HashSet<string> _dirs = new();
        private readonly Dictionary<string, DateTime> _dirTimes = new();

        /// <summary>Creation times the remote reports, by normalized path; absent = not reported.</summary>
        private readonly Dictionary<string, DateTime> _createdTimes = new();

        /// <summary>Folder times the sync engine stamped, by normalized path.</summary>
        public Dictionary<string, DateTime> DirStamps { get; } = new();

        /// <summary>Creation times the sync engine sent with uploads and folder stamps.</summary>
        public Dictionary<string, DateTime?> SentCreated { get; } = new();

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

        /// <summary>Makes the listing report a creation time for an existing file or folder.</summary>
        public void SetCreated(string path, DateTime created) => _createdTimes[Normalize(path)] = created;

        public void RemoveFile(string path) => _files.Remove(Normalize(path));

        public bool HasFile(string path) => _files.ContainsKey(Normalize(path));

        public string ReadText(string path)
            => System.Text.Encoding.UTF8.GetString(_files[Normalize(path)].Data);

        public Task TrySetDirectoryModifiedTimeAsync(string path, DateTime modifiedUtc, CancellationToken ct = default)
        {
            DirStamps[Normalize(path)] = modifiedUtc;
            return Task.CompletedTask;
        }

        public Task TrySetDirectoryTimesAsync(
            string path, DateTime modifiedUtc, DateTime? createdUtc, CancellationToken ct = default)
        {
            SentCreated[Normalize(path)] = createdUtc;
            return TrySetDirectoryModifiedTimeAsync(path, modifiedUtc, ct);
        }

        public Task UploadAsync(
            string path, Stream data, DateTime modifiedTime, DateTime? createdTime, CancellationToken ct = default)
        {
            SentCreated[Normalize(path)] = createdTime;
            return UploadAsync(path, data, modifiedTime, ct);
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

        // Downloads of a path listed here deliver half of the file, then fail with a transient
        // error, the given number of times.
        public Dictionary<string, int> FailingDownloads { get; } = new();

        /// <summary>Thrown by every download instead of delivering data.</summary>
        public Exception? DownloadError { get; set; }

        public int DownloadAttempts { get; private set; }

        /// <summary>Runs after the data arrived, before the download returns: simulates a concurrent local change.</summary>
        public Action? DuringDownload { get; set; }

        public async Task DownloadAsync(string path, Stream target, CancellationToken ct = default)
        {
            DownloadAttempts++;
            if (DownloadError is { } error)
                throw error;
            string key = Resolve(Normalize(path));
            byte[] data = _files[key].Data;
            if (FailingDownloads.TryGetValue(key, out int remaining) && remaining > 0)
            {
                FailingDownloads[key] = remaining - 1;
                await target.WriteAsync(data.AsMemory(0, data.Length / 2), ct);
                throw new IOException($"transient download failure for {key}");
            }
            await target.WriteAsync(data, ct);
            DuringDownload?.Invoke();
        }

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

        // Listing hooks: parallelism the engine may use, a delay per listing (to observe
        // overlap), the highest number of listings seen in flight at once, and failures:
        // transient ones (count down, then succeed) and permanent ones, by normalized path.
        public int ListConcurrency { get; set; } = 1;
        public TimeSpan ListDelay { get; set; }
        public int MaxListsInFlight { get; private set; }
        public Dictionary<string, int> TransientLists { get; } = new();
        public HashSet<string> HardFailLists { get; } = new();
        private int _listsInFlight;

        // Whole-tree listing: off by default (per-directory walk); can throw or omit folders.
        public bool SupportsTreeListing { get; set; }
        public bool TreeListingThrows { get; set; }
        public HashSet<string> TreeListingOmits { get; } = new();
        public int TreeListCalls { get; private set; }
        // Invoked when a whole-tree listing starts, to observe what was reported before it.
        public Action? TreeListingStarted { get; set; }

        public Task<IReadOnlyDictionary<string, IReadOnlyList<CloudItemMeta>>?> TryListTreeAsync(
            string path, CancellationToken ct = default)
        {
            if (!SupportsTreeListing)
                return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<CloudItemMeta>>?>(null);
            TreeListCalls++;
            TreeListingStarted?.Invoke();
            if (TreeListingThrows)
                throw new IOException("tree listing failed");
            string root = Normalize(path);
            var tree = new Dictionary<string, IReadOnlyList<CloudItemMeta>>();
            void Walk(string dir)
            {
                var children = Children(dir);
                if (!TreeListingOmits.Contains(dir))
                    tree[dir == root ? "" : dir[(root.Length == 0 ? 0 : root.Length + 1)..]] = children;
                // Like a server, the walk follows links until it reaches its path length limit.
                if (Depth(dir) < MaxPathDepth)
                    foreach (var child in children.Where(item => item.IsDirectory))
                        Walk(Join(dir, child.Name));
            }
            Walk(root);
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<CloudItemMeta>>?>(tree);
        }

        // Folder links (path → target folder), followed like a Samba server does: a link lists as a
        // folder with its target's time and content. Paths deeper than MaxPathDepth fail to list,
        // like a server at its path length limit.
        public Dictionary<string, string> Links { get; } = new();
        private const int MaxPathDepth = 12;

        public void PutLink(string path, string target) => Links[Normalize(path)] = Normalize(target);

        private static int Depth(string key) => key.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

        private static string Join(string parent, string name) => parent.Length == 0 ? name : $"{parent}/{name}";

        private string Resolve(string key)
        {
            if (Depth(key) > MaxPathDepth)
                throw new UnauthorizedAccessException($"path too long: {key}");
            for (bool resolved = true; resolved;)
            {
                resolved = false;
                foreach (var (link, target) in Links)
                    if (key == link || key.StartsWith(link + "/", StringComparison.Ordinal))
                    {
                        key = target + key[link.Length..];
                        resolved = true;
                        break;
                    }
            }
            return key;
        }

        public async Task<IReadOnlyList<CloudItemMeta>> ListAsync(string path, CancellationToken ct = default)
        {
            string key = Normalize(path);
            lock (ListCalls)
            {
                ListCalls[key] = ListCalls.TryGetValue(key, out int n) ? n + 1 : 1;
                MaxListsInFlight = Math.Max(MaxListsInFlight, ++_listsInFlight);
            }
            try
            {
                if (ListDelay > TimeSpan.Zero)
                    await Task.Delay(ListDelay, ct);
                lock (ListCalls)
                {
                    if (HardFailLists.Contains(key))
                        throw new UnauthorizedAccessException($"list denied for {key}");
                    if (TransientLists.TryGetValue(key, out int remaining) && remaining > 0)
                    {
                        TransientLists[key] = remaining - 1;
                        throw new IOException($"transient list failure for {key}");
                    }
                }
                return Children(key);
            }
            finally
            {
                lock (ListCalls)
                    _listsInFlight--;
            }
        }

        private IReadOnlyList<CloudItemMeta> Children(string key)
        {
            string real = Resolve(key);
            var items = new List<CloudItemMeta>();
            foreach (var (file, value) in _files.Where(f => Parent(f.Key) == real))
                items.Add(new CloudItemMeta(false, NameOf(file), "/" + Join(key, NameOf(file)),
                    value.Data.Length, value.Modified, CreatedOf(file)));
            foreach (var dir in _dirs.Where(d => Parent(d) == real))
                items.Add(new CloudItemMeta(true, NameOf(dir), "/" + Join(key, NameOf(dir)), 0, DirTime(dir), CreatedOf(dir)));
            foreach (var (link, target) in Links.Where(l => Parent(l.Key) == real))
                items.Add(new CloudItemMeta(true, NameOf(link), "/" + Join(key, NameOf(link)), 0, DirTime(Resolve(target)), null));
            return items;
        }

        private DateTime DirTime(string dir) => _dirTimes.TryGetValue(dir, out var time) ? time : DateTime.UnixEpoch;

        private DateTime? CreatedOf(string path) => _createdTimes.TryGetValue(path, out var created) ? created : null;
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

        /// <summary>Creation times set on pulled files and folders, and reported by listings.</summary>
        public Dictionary<string, DateTime> Created { get; } = new();

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
                    Size = value.Data.Length, ModifiedAt = value.Modified,
                    CreatedAt = Created.TryGetValue(file, out var created) ? created : DateTime.MinValue
                });
            foreach (var dir in _dirs.Where(d => Parent(d) == key))
                items.Add(new FileMetadata
                {
                    Name = NameOf(dir), Path = dir, IsDirectory = true,
                    ModifiedAt = _dirTimes.TryGetValue(dir, out var time) ? time : DateTime.UnixEpoch,
                    CreatedAt = Created.TryGetValue(dir, out var created) ? created : DateTime.MinValue
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
        {
            string key = Normalize(path);
            if (_files.TryGetValue(key, out var file))
                return Task.FromResult(new FileMetadata
                {
                    Path = key, IsDirectory = false, Size = file.Data.Length, ModifiedAt = file.Modified
                });
            return DirExists(path)
                ? Task.FromResult(new FileMetadata { Path = key, IsDirectory = true })
                : throw new FileNotFoundException($"'{path}' not found");
        }

        /// <summary>Folders the engine asked to clean of abandoned uploads.</summary>
        public List<string> AbandonedUploadSweeps { get; } = [];

        public Exception? AbandonedUploadSweepError { get; set; }

        public Task DeleteAbandonedUploadsAsync(string directoryPath, UserContext user)
        {
            AbandonedUploadSweeps.Add(Normalize(directoryPath));
            return AbandonedUploadSweepError is { } error ? Task.FromException(error) : Task.CompletedTask;
        }

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

        public async Task SetModifiedAtAsync(string path, UserContext user, DateTime? modifiedAtUtc, DateTime? createdAtUtc)
        {
            if (createdAtUtc is { } created)
                Created[Normalize(path)] = created;
            if (modifiedAtUtc is { } modified)
                await SetModifiedAtAsync(path, user, modified);
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
            => throw new NotSupportedException();

        // Like the real storage: nothing is published unless writeContent completes.
        public async Task ReplaceFileAsync(string path, Func<Stream, CancellationToken, Task> writeContent, UserContext user, FileTimes sourceTimes, CancellationToken ct = default)
        {
            string key = Normalize(path);
            using var unpublished = new MemoryStream();
            await writeContent(unpublished, ct);
            // Same time rules as FileService: the mtime only moves forward (otherwise the file keeps
            // its write time), the creation time only moves back, the access time follows the source.
            DateTime source = sourceTimes.LastWritten ?? DateTime.UnixEpoch;
            bool overwrite = _files.TryGetValue(key, out var previous);
            _files[key] = (unpublished.ToArray(), !overwrite || source > previous.Modified ? source : DateTime.UtcNow);
            if (sourceTimes.LastAccessed is { } accessed)
                Accessed[key] = accessed;
            if (sourceTimes.Created is { } created
                && (!Created.TryGetValue(key, out var knownCreated) || created < knownCreated))
                Created[key] = created;
            Log.Add("write:" + key);
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
