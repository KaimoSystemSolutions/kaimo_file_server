using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Search;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Core.Services.File;

public class FileService : IFileService
{
    private sealed class FileSession : IFileSession
    {
        private readonly IStorageHandle _handle;
        private readonly FileService _owner;
        private readonly bool _isNewFile;
        private bool _disposed;

        public string RelativePath { get; private set; }
        public string AbsolutePath => _handle.AbsolutePath;
        public bool IsDirectory => _handle.IsDirectory;
        public long Length => _handle.Length;
        public UserContext User { get; }
        public bool IsReadOnly { get; }

        public FileSession(
            IStorageHandle handle, FileService owner,
            string relativePath, UserContext user, bool isNewFile, bool isReadOnly)
        {
            _handle = handle;
            _owner = owner;
            RelativePath = relativePath;
            User = user;
            _isNewFile = isNewFile;
            IsReadOnly = isReadOnly;
        }

        public ValueTask<int> ReadAsync(long o, Memory<byte> b, CancellationToken ct)
            => _handle.ReadAsync(o, b, ct);
        public ValueTask WriteAsync(long o, ReadOnlyMemory<byte> d, CancellationToken ct)
            => _handle.WriteAsync(o, d, ct);
        public ValueTask SetLengthAsync(long l, CancellationToken ct)
            => _handle.SetLengthAsync(l, ct);
        public ValueTask SetTimesAsync(FileTimes t, CancellationToken ct)
            => _handle.SetTimesAsync(t, ct);
        public ValueTask FlushAsync(CancellationToken ct) => _handle.FlushAsync(ct);

        public void MarkDeleteOnClose() => _handle.MarkDeleteOnClose();

        public async ValueTask RenameAsync(
            string newRelativePath, bool replaceExisting, CancellationToken ct = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FileSession));

            var oldRel = RelativePath;
            var newRel = ShareRelativePath.Normalize(newRelativePath);
            if (string.Equals(oldRel, newRel, StringComparison.Ordinal))
                return;

            bool isDir = _handle.IsDirectory;

            // ACL: the source needs Delete, the destination needs Create/Write.
            if (!await _owner._acl.HasAccessAsync(
                    User, _owner._shareId, oldRel, isDir, FilePermission.Delete))
                throw new UnauthorizedAccessException($"Rename (delete) denied for '{oldRel}'");

            if (!await _owner._acl.HasAccessAsync(
                    User, _owner._shareId, newRel, isDir, FilePermission.CreateWriteData))
                throw new UnauthorizedAccessException($"Rename (create) denied for '{newRel}'");

            // Overwriting an existing target additionally requires Delete on it.
            if (replaceExisting
                && await _owner._storage.ExistsAsync(newRel)
                && !await _owner._acl.HasAccessAsync(
                        User, _owner._shareId, newRel, isDir, FilePermission.Delete))
                throw new UnauthorizedAccessException($"Rename (overwrite) denied for '{newRel}'");

            var oldAbs = _handle.AbsolutePath;
            var newAbs = _owner._storage.ToAbsolutePath(newRel);
            await _handle.MoveAsync(newAbs, newRel, replaceExisting, ct);

            // Keep the ACL records aligned with the new path.
            await _owner._acl.RenameAclPathAsync(_owner._shareId, oldRel, newRel);

            // Keep the search index aligned with the new path.
            if (isDir)
                _owner.onDirectoryRenamed(oldAbs, newAbs);
            else
                _owner.onFileRenamed(oldAbs, newAbs);

            RelativePath = newRel;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            var abs = _handle.AbsolutePath;
            var rel = RelativePath;
            var isDirty = _handle.IsDirty;
            var isDir = _handle.IsDirectory;
            var deleting = _handle.DeleteOnClose;

            // === Pre-close hooks (need the open stream) ===
            // Versioning: snapshot the file content while the stream is still open.
            if (isDirty && !isDir && !deleting && _owner._versionService != null)
            {
                try
                {
                    await _handle.FlushAsync();
                    var snap = _handle.GetReadableSnapshot();
                    if (snap != null)
                    {
                        await _owner._versionService.CreateVersionAsync(
                            rel, snap, User.User.Id.ToString());
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FileSession Versioning] {rel}: {ex.Message}");
                }
            }

            // === Close the underlying handle (this may delete the file) ===
            await _handle.DisposeAsync();

            // === Post-close hooks (just need the path) ===
            try
            {
                if (deleting)
                {
                    if (isDir) _owner.onDirectoryDeleted(abs);
                    else _owner.onFileDeleted(abs);
                }
                else if (_isNewFile && isDir)
                {
                    _owner.onDirectoryCreated(abs);
                }
                else if (isDirty && !isDir)
                {
                    // Re-open through storage for search indexing — closed stream
                    // means no race with the SMB session. The Task<Stream> contract
                    // your search hook expects is preserved.
                    _owner.OnFileCreated(abs, _owner._storage.ReadAsync(rel));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileSession SearchHook] {rel}: {ex.Message}");
            }
        }
    }

    private sealed class ReadOnlySnapshotSession : IFileSession
    {
        private readonly Stream _stream;
        private bool _disposed;

        public string RelativePath { get; }
        public string AbsolutePath => RelativePath;
        public bool IsDirectory => false;
        public long Length => _stream.Length;
        public UserContext User { get; }
        public bool IsReadOnly => true;

        public ReadOnlySnapshotSession(Stream stream, string relativePath, UserContext user)
        {
            _stream = stream;
            RelativePath = relativePath;
            User = user;
        }

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            if (_stream.Position != offset) _stream.Position = offset;
            return await _stream.ReadAsync(buffer, ct);
        }

        public ValueTask WriteAsync(long o, ReadOnlyMemory<byte> d, CancellationToken ct)
            => throw new UnauthorizedAccessException("Snapshot is read-only.");
        public ValueTask SetLengthAsync(long l, CancellationToken ct)
            => throw new UnauthorizedAccessException("Snapshot is read-only.");
        public ValueTask SetTimesAsync(FileTimes t, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RenameAsync(string newRelativePath, bool replaceExisting, CancellationToken ct)
            => throw new UnauthorizedAccessException("Snapshot is read-only.");
        public void MarkDeleteOnClose() => throw new UnauthorizedAccessException("Snapshot is read-only.");

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }


    private readonly IStorageEngine _storage;
    private readonly IAclService _acl;
    private readonly ISearchService? _searchService;
    private readonly IFileVersionService? _versionService;
    private readonly Guid _shareId;

    private const string RecycleBinFolder = ".RECYCLE_BIN";

    public FileService(
    IStorageEngine storage,
    IAclService acl,
    ISearchService searchService,
    Guid shareId,
    IFileVersionService? versionService = null)
    {
        _storage = storage;
        _acl = acl;
        _searchService = searchService;
        _shareId = shareId;
        _versionService = versionService;
    }


    public void OnFileCreated(string absolutePath, Task<Stream> fileData)
    {
        _searchService?.onFileCreated(absolutePath, fileData);
    }
    
    public void onDirectoryCreated(string absolutePath)
    {
        _searchService?.onDirectoryCreated(absolutePath);
    }

    public void onFileDeleted(string absolutePath)
    {
        _searchService?.onFileDeleted(absolutePath);
    }
    
    public void onDirectoryDeleted(string absolutePath)
    {
        _searchService?.onDirectoryDeleted(absolutePath);
    }

    public void onFileRenamed(string oldAbsolutePath, string newAbsolutePath)
    {
        _searchService?.onFileRenamed(oldAbsolutePath, newAbsolutePath);
    }

    public void onDirectoryRenamed(string oldAbsolutePath, string newAbsolutePath)
    {
        _searchService?.onDirectoryRenamed(oldAbsolutePath, newAbsolutePath);
    }

    // ------------------ ACL helpers ------------------

    /// <summary>
    /// Normalizes <paramref name="path"/>, resolves whether it is a directory from
    /// storage, and returns whether <paramref name="user"/> holds <paramref name="permission"/>.
    /// Shared by the boolean Can*Async probes.
    /// </summary>
    private async Task<bool> CheckAccessAsync(string path, UserContext user, FilePermission permission)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);
        return await _acl.HasAccessAsync(user, _shareId, normalized, isDir, permission);
    }

    /// <summary>
    /// Throws <see cref="UnauthorizedAccessException"/> unless <paramref name="user"/> holds
    /// <paramref name="permission"/> on the already-normalized <paramref name="normalizedPath"/>.
    /// Centralises the "check-or-throw" that every mutating/reading operation performs.
    /// </summary>
    private async Task EnsureAccessAsync(
        UserContext user, string normalizedPath, bool isDirectory, FilePermission permission)
    {
        if (!await _acl.HasAccessAsync(user, _shareId, normalizedPath, isDirectory, permission))
            throw new UnauthorizedAccessException(
                $"Access denied ({permission}) for '{normalizedPath}'");
    }

    // ------------------ Directory Listing ------------------

    public async Task<List<FileMetadata>> ListAsync(string directoryPath, UserContext user)
    {
        var normalizedDir = ShareRelativePath.Normalize(directoryPath);

        if (!await _acl.HasAccessAsync(user, _shareId, normalizedDir, true, FilePermission.ListReadData))
            throw new UnauthorizedAccessException($"List denied for '{normalizedDir}'");

        var items = await _storage.ListAsync(normalizedDir);

        var itemsToCheck = items
            .Select(i => (ShareRelativePath.Combine(normalizedDir, i.Name), i.IsDirectory))
            .ToList();

        var accessMap = await _acl.HasAccessBatchAsync(
            user, _shareId, itemsToCheck, FilePermission.ListReadData);

        var visible = new List<FileMetadata>(items.Count);
        foreach (var item in items)
        {
            var itemPath = ShareRelativePath.Combine(normalizedDir, item.Name);
            if (accessMap.TryGetValue(itemPath, out var allowed) && allowed)
                visible.Add(item);
        }

        return visible;
    }

    // ------------------ Batch Permission Check ------------------

    public async Task<HashSet<string>> FilterReadablePathsAsync(
        IReadOnlyList<(string relativePath, bool isDirectory)> items,
        UserContext user)
    {
        if (items.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var normalized = items
            .Select(i => (ShareRelativePath.Normalize(i.relativePath), i.isDirectory))
            .ToList();

        var accessMap = await _acl.HasAccessBatchAsync(
            user, _shareId, normalized, FilePermission.ListReadData);

        var readable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, allowed) in accessMap)
        {
            if (allowed)
                readable.Add(path);
        }

        return readable;
    }

    // ------------------ Permission Checks ------------------

    public Task<bool> CanReadAsync(string path, UserContext user)
        => CheckAccessAsync(path, user, FilePermission.ListReadData);

    public Task<bool> CanWriteAsync(string path, UserContext user)
        => CheckAccessAsync(path, user, FilePermission.CreateWriteData);

    public async Task<bool> CanCreateAsync(string path, UserContext user)
    {
        var parentPath = ShareRelativePath.GetParent(path);
        return await _acl.HasAccessAsync(user, _shareId, parentPath, true, FilePermission.CreateWriteData);
    }

    public Task<bool> CanDeleteAsync(string path, UserContext user)
        => CheckAccessAsync(path, user, FilePermission.Delete);

    public async Task<bool> CanListAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        return await _acl.HasAccessAsync(user, _shareId, normalized, true, FilePermission.ListReadData);
    }

    // ------------------ Full Operations ------------------

    public async Task<Stream> ReadFileAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);

        await EnsureAccessAsync(user, normalized, isDir, FilePermission.ListReadData);

        return await _storage.ReadAsync(normalized);
    }

    public async Task WriteFileAsync(string path, Stream data, UserContext user, CancellationToken cancellationToken = default)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);

        await EnsureAccessAsync(user, normalized, isDir, FilePermission.CreateWriteData);

        await _storage.WriteAsync(normalized, data, cancellationToken);

        OnFileCreated(ToAbsolutePath(path), _storage.ReadAsync(normalized));
    }

    public string ToAbsolutePath(string path)
    {
        var normalized = ShareRelativePath.Normalize(path);
        return _storage.ToAbsolutePath(normalized);
    }
    
    public async Task ArchiveAsync(List<string> sourcePaths, string targetPath, string format, UserContext user)
    {
        var normalizedTarget = ShareRelativePath.Normalize(targetPath);

        // Check read on all sources
        foreach (var source in sourcePaths)
        {
            var normalized = ShareRelativePath.Normalize(source);
            var isDir = await _storage.IsDirectoryAsync(normalized);
            await EnsureAccessAsync(user, normalized, isDir, FilePermission.ListReadData);
        }

        // Check write on target directory
        var parentDir = ShareRelativePath.GetParent(normalizedTarget);
        await EnsureAccessAsync(user, parentDir, true, FilePermission.CreateWriteData);

        await _storage.ArchiveAsync(
            sourcePaths.Select(ShareRelativePath.Normalize).ToList(),
            normalizedTarget,
            format);
    }
    
    public async Task UnzipAsync(string zipPath, string targetPath, UserContext user)
    {
        var normalizedZip = ShareRelativePath.Normalize(zipPath);
        var normalizedTarget = ShareRelativePath.Normalize(targetPath);

        // Check read permission on the zip
        await EnsureAccessAsync(user, normalizedZip, false, FilePermission.ListReadData);

        // Check write permission on the target directory
        var parentDir = ShareRelativePath.GetParent(normalizedTarget);
        await EnsureAccessAsync(user, parentDir, true, FilePermission.CreateWriteData);

        await _storage.UnzipAsync(normalizedZip, normalizedTarget);
    }
    

    public async Task CreateFileAsync(string path, UserContext user)
    {
        var parentPath = ShareRelativePath.GetParent(path);

        await EnsureAccessAsync(user, parentPath, true, FilePermission.CreateWriteData);

        await _storage.WriteAsync(ShareRelativePath.Normalize(path), Stream.Null);
    }

    public async Task CreateDirectoryAsync(string path, UserContext user)
    {
        var parentPath = ShareRelativePath.GetParent(path);

        await EnsureAccessAsync(user, parentPath, true, FilePermission.CreateWriteData);

        await _storage.CreateDirectory(ShareRelativePath.Normalize(path));
        onDirectoryCreated(ToAbsolutePath(path));
    }

    public async Task DeleteFileAsync(string path, UserContext user, bool isRecycleEnabled)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);

        await EnsureAccessAsync(user, normalized, isDir, FilePermission.Delete);

        var isAlreadyInRecycleBin = normalized.StartsWith(
            RecycleBinFolder, StringComparison.OrdinalIgnoreCase);

        var absolutePath = ToAbsolutePath(path);
        
        if(isDir)
            onDirectoryDeleted(absolutePath);
        else
            onFileDeleted(absolutePath);
        
        if (isRecycleEnabled && !isAlreadyInRecycleBin)
        {
            var recyclePath = ShareRelativePath.Combine(RecycleBinFolder, normalized);
            await _storage.MoveAsync(normalized, recyclePath);
            await _acl.RenameAclPathAsync(_shareId, normalized, recyclePath);
        }
        else
        {
            await _storage.DeleteAsync(normalized);
            await _acl.DeleteAclAsync(_shareId, normalized);
        }
    }

    public async Task<FileMetadata> GetMetadataAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var meta = await _storage.GetMetadataAsync(normalized);

        await EnsureAccessAsync(user, normalized, meta.IsDirectory, FilePermission.ListReadData);

        return meta;
    }

    public async Task RenameAsync(string oldPath, string newPath, UserContext user)
    {
        var oldNormalized = ShareRelativePath.Normalize(oldPath);
        var newNormalized = ShareRelativePath.Normalize(newPath);
        var isDir = await _storage.IsDirectoryAsync(oldNormalized);

        // A rename is a delete at the source plus a create at the destination.
        await EnsureAccessAsync(user, oldNormalized, isDir, FilePermission.Delete);
        await EnsureAccessAsync(user, newNormalized, isDir, FilePermission.CreateWriteData);

        // Capture absolute paths up front — the mapping is a pure string
        // transform, so it is valid before and after the physical move.
        var oldAbs = ToAbsolutePath(oldNormalized);
        var newAbs = ToAbsolutePath(newNormalized);

        if (isDir)
        {
            await _storage.RenameDirectoryAsync(oldNormalized, newNormalized);
            await _acl.RenameAclPathAsync(_shareId, oldNormalized, newNormalized);
            onDirectoryRenamed(oldAbs, newAbs);
        }
        else
        {
            await _storage.RenameFileAsync(oldNormalized, newNormalized);
            await _acl.RenameAclPathAsync(_shareId, oldNormalized, newNormalized);
            onFileRenamed(oldAbs, newAbs);
        }
    }

    public async Task<long> GetDirectorySizeAsync(string relativePath, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(relativePath);

        await EnsureAccessAsync(user, normalized, true, FilePermission.ListReadData);

        return await _storage.GetDirectorySizeAsync(normalized);
    }

    public async Task<FileOpenResult> OpenAsync(
    string path, OpenMode mode, AccessIntent intent, ShareIntent share,
    UserContext user, CancellationToken ct = default)
    {
        var normalized = ShareRelativePath.Normalize(path);

        bool exists = await _storage.ExistsAsync(normalized);
        bool isDir = exists && await _storage.IsDirectoryAsync(normalized);
        bool wantsWrite = (intent & AccessIntent.Write) != 0;
        bool wantsCreate = mode is OpenMode.Create or OpenMode.OpenOrCreate
                                  or OpenMode.CreateOrTruncate or OpenMode.Supersede;

        // Single permission check at open. No more per-Read/per-Write checks.
        if (!exists)
        {
            if (!wantsCreate)
                throw new FileNotFoundException($"'{normalized}' not found");

            var parent = ShareRelativePath.GetParent(normalized);
            if (!await _acl.HasAccessAsync(user, _shareId, parent, true, FilePermission.CreateWriteData))
                throw new UnauthorizedAccessException($"Create denied for '{normalized}'");
        }
        else
        {
            // Enforce EACH requested access independently. A ReadWrite open must satisfy both
            // permissions, and an explicit read-Deny must still block reads even when the client
            // also asks for write. Previously a write intent skipped the read check entirely,
            // which let a write-only (or explicitly read-denied) user read file contents by
            // opening the file with ReadWrite intent — an ACL bypass.
            if (wantsWrite
                && !await _acl.HasAccessAsync(user, _shareId, normalized, isDir, FilePermission.CreateWriteData))
                throw new UnauthorizedAccessException($"Write denied for '{normalized}'");

            // Any open that is not write-only reads the entry (delete-only opens map to Read
            // intent too), so require read unless this is a pure write open.
            bool wantsRead = (intent & AccessIntent.Read) != 0 || !wantsWrite;

            if (wantsRead
                && !await _acl.HasAccessAsync(user, _shareId, normalized, isDir, FilePermission.ListReadData))
                throw new UnauthorizedAccessException($"Read denied for '{normalized}'");
        }

        var storageHandle = await _storage.OpenAsync(normalized, mode, intent, share, ct);

        var status = (mode, exists) switch
        {
            (OpenMode.Create, _) => FileOpenStatus.Created,
            (OpenMode.OpenOrCreate, false) => FileOpenStatus.Created,
            (OpenMode.CreateOrTruncate, false) => FileOpenStatus.Created,
            (OpenMode.CreateOrTruncate, true) => FileOpenStatus.Overwritten,
            (OpenMode.Supersede, false) => FileOpenStatus.Created,
            (OpenMode.Supersede, true) => FileOpenStatus.Superseded,
            (OpenMode.Truncate, _) => FileOpenStatus.Overwritten,
            _ => FileOpenStatus.Opened
        };

        // A handle opened purely for reading must never accept data writes,
        // even if a later request slips through the transport layer.
        bool isReadOnly = !storageHandle.IsDirectory && (intent & AccessIntent.Write) == 0;

        var session = new FileSession(storageHandle, this, normalized, user,
            isNewFile: !exists, isReadOnly: isReadOnly);
        return new FileOpenResult(session, status);
    }

    public async Task<IFileSession> OpenSnapshotAsync(
        string realPath, DateTime ts, UserContext user, CancellationToken ct = default)
    {
        if (_versionService == null)
            throw new NotSupportedException("Versioning is not configured.");

        var normalized = ShareRelativePath.Normalize(realPath);

        await EnsureAccessAsync(user, normalized, false, FilePermission.ListReadData);

        var stream = await _versionService.ReadVersionAsync(normalized, ts);
        return new ReadOnlySnapshotSession(stream, normalized, user);
    }

    public async Task<List<DateTime>> GetSnapshotTimestampsAsync(UserContext user)
    {
        if (_versionService == null) return new List<DateTime>();
        // No ACL check on timestamps themselves — they're just dates.
        // Per-path access is enforced when the user opens an @GMT- path.
        return await _versionService.GetSnapshotTimestampsAsync();
    }
}