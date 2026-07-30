using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Search;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
            RelativePath = newRel;
            await _owner.ApplyRenameSideEffectsAsync(
                oldRel, newRel, isDir, oldAbs, newAbs, bestEffort: false);
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
                            _owner._shareId, rel, snap, User.User.Id.ToString());
                    }
                }
                catch (Exception ex)
                {
                    _owner._logger.LogWarning(LogEvents.FileVersionSnapshotFailed, ex, LogMessages.FileVersionSnapshotFailed, rel);
                }
            }

            // === Close the underlying handle (this may delete the file) ===
            await _handle.DisposeAsync();

            // === Post-close hooks (just need the path) ===
            try
            {
                if (deleting)
                {
                    await _owner.ApplyDeleteSideEffectsAsync(
                        rel, isDir, abs, bestEffort: true);
                }
                else if (_isNewFile && isDir)
                {
                    await _owner.onDirectoryCreated(abs);
                }
                else if (isDirty && !isDir)
                {
                    // Re-open through storage for search indexing — closed stream
                    // means no race with the SMB session. The Task<Stream> contract
                    // your search hook expects is preserved.
                    await _owner.OnFileCreated(abs, _owner._storage.ReadAsync(rel));
                }
            }
            catch (Exception ex)
            {
                _owner._logger.LogWarning(LogEvents.FileSearchHookFailed, ex, LogMessages.FileSearchHookFailed, rel);
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
    private readonly IFileOwnershipService? _ownershipService;
    private readonly ILogger<FileService> _logger;
    private readonly Guid _shareId;

    private const string RecycleBinFolder = ".RECYCLE_BIN";

    public FileService(
    IStorageEngine storage,
    IAclService acl,
    ISearchService searchService,
    Guid shareId,
    IFileVersionService? versionService = null,
    IFileOwnershipService? ownershipService = null,
    ILogger<FileService>? logger = null)
    {
        _storage = storage;
        _acl = acl;
        _searchService = searchService;
        _shareId = shareId;
        _versionService = versionService;
        _ownershipService = ownershipService;
        _logger = logger ?? NullLogger<FileService>.Instance;
    }

    /// <summary>
    /// Persists the creating user as the owner of a freshly created item. Best-effort:
    /// a failure here (e.g. a transient DB error or a create race) must never fail the
    /// underlying file operation, so it is swallowed and logged. No-op when ownership
    /// tracking is not configured.
    /// </summary>
    private async Task RecordOwnerAsync(
        string normalizedPath,
        bool isDirectory,
        UserContext user,
        bool bestEffort = true)
    {
        if (_ownershipService is null) return;
        try
        {
            await _ownershipService.EnsureOwnerAsync(_shareId, normalizedPath, isDirectory, user.User.Id);
        }
        catch (Exception ex)
        {
            if (!bestEffort) throw;
            _logger.LogWarning(LogEvents.FileOwnerRecordFailed, ex, LogMessages.FileOwnerRecordFailed, normalizedPath);
        }
    }

    private async Task ApplyDeleteSideEffectsAsync(
        string relativePath, bool isDirectory, string absolutePath, bool bestEffort)
    {
        var failures = new List<Exception>();

        try { await _acl.DeleteAclAsync(_shareId, relativePath); }
        catch (Exception ex) { failures.Add(ex); }

        if (_versionService != null)
        {
            try { await _versionService.DeletePathAsync(_shareId, relativePath); }
            catch (Exception ex) { failures.Add(ex); }
        }

        if (_searchService != null)
        {
            try
            {
                if (isDirectory) await _searchService.onDirectoryDeleted(absolutePath);
                else await _searchService.onFileDeleted(absolutePath);
            }
            catch (Exception ex) { failures.Add(ex); }
        }

        if (failures.Count == 0) return;
        var aggregate = new AggregateException(
            $"One or more delete side effects failed for '{relativePath}'.", failures);
        if (!bestEffort) throw aggregate;
        _logger.LogWarning(aggregate, "Delete lifecycle cleanup failed for {Path}", relativePath);
    }

    private async Task ApplyRenameSideEffectsAsync(
        string oldRelativePath, string newRelativePath, bool isDirectory,
        string oldAbsolutePath, string newAbsolutePath, bool bestEffort,
        Guid? sambaLifecycleEventId = null)
    {
        var failures = new List<Exception>();

        try { await _acl.RenameAclPathAsync(_shareId, oldRelativePath, newRelativePath); }
        catch (Exception ex) { failures.Add(ex); }

        if (_versionService != null)
        {
            try
            {
                await _versionService.RenamePathAsync(
                    _shareId, oldRelativePath, newRelativePath,
                    sambaLifecycleEventId);
            }
            catch (Exception ex) { failures.Add(ex); }
        }

        if (_searchService != null)
        {
            try
            {
                if (isDirectory) await _searchService.onDirectoryRenamed(oldAbsolutePath, newAbsolutePath);
                else await _searchService.onFileRenamed(oldAbsolutePath, newAbsolutePath);
            }
            catch (Exception ex) { failures.Add(ex); }
        }

        if (failures.Count == 0) return;
        var aggregate = new AggregateException(
            $"One or more rename side effects failed for '{oldRelativePath}' -> '{newRelativePath}'.",
            failures);
        if (!bestEffort) throw aggregate;
        _logger.LogWarning(aggregate, "Rename lifecycle cleanup failed for {OldPath} -> {NewPath}",
            oldRelativePath, newRelativePath);
    }


    public Task OnFileCreated(string absolutePath, Task<Stream> fileData)
        => _searchService?.onFileCreated(absolutePath, fileData) ?? DrainStreamAsync(fileData);

    public Task onDirectoryCreated(string absolutePath)
        => _searchService?.onDirectoryCreated(absolutePath) ?? Task.CompletedTask;

    public Task onFileDeleted(string absolutePath)
        => _searchService?.onFileDeleted(absolutePath) ?? Task.CompletedTask;
    
    public Task onDirectoryDeleted(string absolutePath)
        => _searchService?.onDirectoryDeleted(absolutePath) ?? Task.CompletedTask;

    private static async Task DrainStreamAsync(Task<Stream> streamTask)
    {
        await using var stream = await streamTask;
    }

    public void onFileRenamed(string oldAbsolutePath, string newAbsolutePath)
    {
        _searchService?.onFileRenamed(oldAbsolutePath, newAbsolutePath);
    }

    public void onDirectoryRenamed(string oldAbsolutePath, string newAbsolutePath)
    {
        _searchService?.onDirectoryRenamed(oldAbsolutePath, newAbsolutePath);
    }

    // ------------------ External-writer close hooks (Samba VFS direct I/O) ------------------
    //
    // Samba performs the raw file I/O itself, then the VFS bridge calls these so the same
    // cross-cutting effects as FileSession.DisposeAsync happen: versioning, search indexing,
    // ownership. P1-11 surfaces failures to the bridge so authd retains and
    // retries the durable event. The native SMB operation has already completed.

    public async Task NotifyExternalCloseAsync(
        string path,
        UserContext user,
        Func<Task<Stream>> openCapturedContent)
    {
        ArgumentNullException.ThrowIfNull(openCapturedContent);
        var rel = ShareRelativePath.Normalize(path);
        var abs = _storage.ToAbsolutePath(rel);
        var failures = new List<Exception>();

        try
        {
            await RecordOwnerAsync(
                rel, isDirectory: false, user, bestEffort: false);
        }
        catch (Exception ex) { failures.Add(ex); }

        // Versioning: snapshot the freshly written content (mirrors DisposeAsync).
        if (_versionService != null)
        {
            try
            {
                await using var content = await openCapturedContent();
                await _versionService.CreateVersionAsync(_shareId, rel, content, user.User.Id.ToString());
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        // Search index. Awaited (not fire-and-forget like the in-process DisposeAsync):
        // the bridge runs this inside a short-lived gRPC scope, so we must let it finish
        // before the scope/DbContext is torn down — and surface errors.
        if (_searchService != null)
        {
            try { await _searchService.onFileCreated(abs, openCapturedContent()); }
            catch (Exception ex) { failures.Add(ex); }
        }

        if (failures.Count != 0)
            throw new AggregateException(
                $"One or more close side effects failed for '{rel}'.",
                failures);
    }

    public async Task NotifyExternalMkdirAsync(string path, UserContext user)
    {
        var rel = ShareRelativePath.Normalize(path);
        var abs = _storage.ToAbsolutePath(rel);
        var failures = new List<Exception>();
        try
        {
            await RecordOwnerAsync(
                rel, isDirectory: true, user, bestEffort: false);
        }
        catch (Exception ex) { failures.Add(ex); }
        if (_searchService != null)
        {
            try { await _searchService.onDirectoryCreated(abs); }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (failures.Count != 0)
            throw new AggregateException(
                $"One or more mkdir side effects failed for '{rel}'.",
                failures);
    }

    public async Task NotifyExternalDeleteAsync(string path, bool isDirectory)
    {
        var rel = ShareRelativePath.Normalize(path);
        var abs = _storage.ToAbsolutePath(rel);
        await ApplyDeleteSideEffectsAsync(rel, isDirectory, abs, bestEffort: false);
    }

    public async Task NotifyExternalRenameAsync(
        string oldPath,
        string newPath,
        bool isDirectory,
        Guid sambaLifecycleEventId)
    {
        var oldRel = ShareRelativePath.Normalize(oldPath);
        var newRel = ShareRelativePath.Normalize(newPath);
        var oldAbs = _storage.ToAbsolutePath(oldRel);
        var newAbs = _storage.ToAbsolutePath(newRel);

        await ApplyRenameSideEffectsAsync(
            oldRel, newRel, isDirectory, oldAbs, newAbs, bestEffort: false,
            sambaLifecycleEventId);
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

        // Never represent a missing path (or a file path) as an empty directory.
        // Keep this check after authorization so callers cannot use the different
        // error types to probe the existence of paths they are not allowed to see.
        if (!await _storage.ExistsAsync(normalizedDir)
            || !await _storage.IsDirectoryAsync(normalizedDir))
        {
            throw new DirectoryNotFoundException(
                $"Directory '{normalizedDir}' does not exist.");
        }

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

    public async Task SetModifiedAtAsync(string path, UserContext user, DateTime time)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);
        
        if(isDir)
            return;
        
        await EnsureAccessAsync(user, normalized, isDir, FilePermission.CreateWriteData);

        var existedBefore = await _storage.ExistsAsync(normalized);

        if(!existedBefore)
            return;

        await _storage.SetModifiedDateAsync(normalized, time);
    }

    public async Task WriteFileAsync(string path, Stream data, UserContext user, CancellationToken cancellationToken = default)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);

        await EnsureAccessAsync(user, normalized, isDir, FilePermission.CreateWriteData);

        // Distinguish a fresh create from an overwrite so ownership is stamped on the
        // creator, never reassigned to whoever later overwrites the file.
        var existedBefore = await _storage.ExistsAsync(normalized);

        await _storage.WriteAsync(normalized, data, cancellationToken);

        if (!existedBefore)
            await RecordOwnerAsync(normalized, isDirectory: false, user);

        // Versioning: snapshot the freshly-written content so web uploads/overwrites
        // build the same version history that SMB writes do (FileSession.DisposeAsync).
        // Content-addressable dedup skips this when the content is unchanged. A
        // versioning failure must never fail the upload itself.
        if (!isDir && _versionService != null)
        {
            try
            {
                await using var written = await _storage.ReadAsync(normalized);
                await _versionService.CreateVersionAsync(
                    _shareId, normalized, written, user.User.Id.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(LogEvents.FileWriteVersionFailed, ex, LogMessages.FileWriteVersionFailed, normalized);
            }
        }

        // Search indexing is a derived, rebuildable side effect. A transient search
        // outage must not turn a successfully persisted upload into a failed write
        // (the web layer would otherwise delete the file as "partial"). This matches
        // the best-effort semantics used by session close and the SMB bridge.
        var fileData = _storage.ReadAsync(normalized);
        try
        {
            await OnFileCreated(ToAbsolutePath(normalized), fileData);
        }
        catch (Exception ex)
        {
            // The search implementation may fail before it takes ownership of the
            // eagerly opened stream. Dispose it here as a safe, idempotent fallback.
            try { await DrainStreamAsync(fileData); }
            catch { /* the indexing failure is the useful diagnostic */ }

            _logger.LogWarning(
                LogEvents.FileSearchHookFailed, ex,
                LogMessages.FileSearchHookFailed, normalized);
        }
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

        var normalized = ShareRelativePath.Normalize(path);
        await _storage.WriteAsync(normalized, Stream.Null);
        await RecordOwnerAsync(normalized, isDirectory: false, user);
    }

    public async Task CreateDirectoryAsync(string path, UserContext user)
    {
        var parentPath = ShareRelativePath.GetParent(path);

        await EnsureAccessAsync(user, parentPath, true, FilePermission.CreateWriteData);

        var normalized = ShareRelativePath.Normalize(path);
        await _storage.CreateDirectory(normalized);
        await RecordOwnerAsync(normalized, isDirectory: true, user);
        await onDirectoryCreated(ToAbsolutePath(path));
    }

    public async Task DeleteFileAsync(string path, UserContext user, bool isRecycleEnabled)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);

        await EnsureAccessAsync(user, normalized, isDir, FilePermission.Delete);

        var isAlreadyInRecycleBin = normalized.StartsWith(
            RecycleBinFolder, StringComparison.OrdinalIgnoreCase);

        var absolutePath = ToAbsolutePath(normalized);
        
        if (isRecycleEnabled && !isAlreadyInRecycleBin)
        {
            var recyclePath = ShareRelativePath.Combine(RecycleBinFolder, normalized);
            // MoveAsync may append a timestamp suffix on a name collision in the
            // recycle bin — align the ACL with the path that actually landed on disk.
            var actualRecyclePath = await _storage.MoveAsync(normalized, recyclePath);
            await ApplyRenameSideEffectsAsync(
                normalized, actualRecyclePath, isDir,
                absolutePath, ToAbsolutePath(actualRecyclePath), bestEffort: false);
        }
        else
        {
            await _storage.DeleteAsync(normalized);
            await ApplyDeleteSideEffectsAsync(
                normalized, isDir, absolutePath, bestEffort: false);
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
        }
        else
        {
            await _storage.RenameFileAsync(oldNormalized, newNormalized);
        }

        await ApplyRenameSideEffectsAsync(
            oldNormalized, newNormalized, isDir, oldAbs, newAbs, bestEffort: false);
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

        // Create-via-open is how the SMB transport creates files — stamp the creator as
        // owner here so SMB writes get the same ownership record as web uploads.
        if (status == FileOpenStatus.Created)
            await RecordOwnerAsync(normalized, storageHandle.IsDirectory, user);

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

        var stream = await _versionService.ReadVersionAsync(_shareId, normalized, ts);
        return new ReadOnlySnapshotSession(stream, normalized, user);
    }

    public async Task<List<DateTime>> GetSnapshotTimestampsAsync(UserContext user)
    {
        if (_versionService == null) return new List<DateTime>();
        // No ACL check on timestamps themselves — they're just dates.
        // Per-path access is enforced when the user opens an @GMT- path.
        // Scoped to this share so snapshots never leak across shares that
        // happen to contain files at the same relative path.
        return await _versionService.GetSnapshotTimestampsAsync(_shareId);
    }

    // ------------------ Versioning (web UI) ------------------

    public async Task<List<FileVersion>> GetFileVersionsAsync(string path, UserContext user)
    {
        if (_versionService == null) return new List<FileVersion>();

        var normalized = ShareRelativePath.Normalize(path);
        await EnsureAccessAsync(user, normalized, false, FilePermission.ListReadData);

        return await _versionService.GetVersionsAsync(_shareId, normalized);
    }

    public async Task<Stream> ReadFileVersionAsync(
        string path, DateTime snapshotTimestampUtc, UserContext user)
    {
        if (_versionService == null)
            throw new NotSupportedException("Versioning is not configured.");

        var normalized = ShareRelativePath.Normalize(path);
        await EnsureAccessAsync(user, normalized, false, FilePermission.ListReadData);

        return await _versionService.ReadVersionAsync(_shareId, normalized, snapshotTimestampUtc);
    }

    public async Task RestoreFileVersionAsync(
        string path, DateTime snapshotTimestampUtc, UserContext user)
    {
        if (_versionService == null)
            throw new NotSupportedException("Versioning is not configured.");

        var normalized = ShareRelativePath.Normalize(path);
        await EnsureAccessAsync(user, normalized, false, FilePermission.CreateWriteData);

        // Snapshot the current content first so restoring is itself undoable.
        // Content-addressable dedup skips this if it equals the latest version.
        if (await _storage.ExistsAsync(normalized))
        {
            await using var current = await _storage.ReadAsync(normalized);
            await _versionService.CreateVersionAsync(_shareId, normalized, current, user.User.Id.ToString());
        }

        // ReadVersionAsync fully decompresses and verifies size/hash before it
        // returns. IStorageEngine.WriteAsync then publishes the verified content
        // atomically, so a corrupt snapshot or failed write leaves the live file
        // untouched.
        await using var restored = await _versionService.ReadVersionAsync(_shareId, normalized, snapshotTimestampUtc);
        await _storage.WriteAsync(normalized, restored);

        await OnFileCreated(ToAbsolutePath(normalized), _storage.ReadAsync(normalized));
    }

    public async Task<List<DateTime>> GetFolderSnapshotTimestampsAsync(
        string folderPath, UserContext user)
    {
        if (_versionService == null) return new List<DateTime>();

        var normalized = ShareRelativePath.Normalize(folderPath);
        await EnsureAccessAsync(user, normalized, true, FilePermission.ListReadData);

        return await _versionService.GetSnapshotTimestampsAsync(_shareId, normalized);
    }

    public async Task<List<FileVersion>> GetFolderSnapshotAsync(
        string folderPath, DateTime asOfUtc, UserContext user)
    {
        if (_versionService == null) return new List<FileVersion>();

        var normalized = ShareRelativePath.Normalize(folderPath);
        await EnsureAccessAsync(user, normalized, true, FilePermission.ListReadData);

        var versions = await _versionService.GetFolderSnapshotAsync(
            _shareId, normalized, asOfUtc);
        if (versions.Count == 0) return versions;

        var readable = await FilterReadablePathsAsync(
            versions.Select(v => (v.FilePath, false)).ToList(), user);

        return versions.Where(v => readable.Contains(v.FilePath)).ToList();
    }

    public async Task<List<FileVersion>> GetFolderSnapshotAsync(
        string folderPath, DateTime asOfUtc, UserContext user,
        CancellationToken cancellationToken)
    {
        if (_versionService == null) return new List<FileVersion>();

        cancellationToken.ThrowIfCancellationRequested();
        var normalized = ShareRelativePath.Normalize(folderPath);
        await EnsureAccessAsync(
                user, normalized, true, FilePermission.ListReadData)
            .WaitAsync(cancellationToken);

        var versions = await _versionService.GetFolderSnapshotAsync(
            _shareId, normalized, asOfUtc, cancellationToken);
        if (versions.Count == 0) return versions;

        // Hide files the user may not read (per-file ACLs can differ from the folder).
        var readable = await FilterReadablePathsAsync(
                versions.Select(v => (v.FilePath, false)).ToList(), user)
            .WaitAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        return versions.Where(v => readable.Contains(v.FilePath)).ToList();
    }
}
