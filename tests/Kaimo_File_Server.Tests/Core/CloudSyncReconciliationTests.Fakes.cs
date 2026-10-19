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
            => DirExists(path) || HasFile(path)
                ? Task.FromResult(new FileMetadata { Path = Normalize(path), IsDirectory = DirExists(path) })
                : throw new FileNotFoundException($"'{path}' not found");

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
