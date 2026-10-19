using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Search;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kaimo_File_Server.Core.Services.File;

public partial class FileService : IFileService
{
    private readonly IStorageEngine _storage;
    private readonly IAclService _acl;
    private readonly ISearchService? _searchService;
    private readonly IFileVersionService? _versionService;
    private readonly IFileOwnershipService? _ownershipService;
    private readonly ICloudSyncPathUpdater? _cloudSyncPathUpdater;
    private readonly ICloudSyncOperationCoordinator? _cloudSyncOperations;
    private readonly IFileChangeLog? _changeLog;
    private readonly ILogger<FileService> _logger;
    private readonly Guid _shareId;
    // ShareDefinition.RecycleRootDepth: where reserved namespaces and the recycle bin live.
    private readonly int _recycleRootDepth;
    private readonly SemaphoreSlim _searchSideEffectLock = new(1, 1);

    public FileService(
    IStorageEngine storage,
    IAclService acl,
    ISearchService? searchService,
    Guid shareId,
    IFileVersionService? versionService = null,
    IFileOwnershipService? ownershipService = null,
    ILogger<FileService>? logger = null,
    ICloudSyncPathUpdater? cloudSyncPathUpdater = null,
    ICloudSyncOperationCoordinator? cloudSyncOperations = null,
    IFileChangeLog? changeLog = null,
    int recycleRootDepth = 0)
    {
        _recycleRootDepth = recycleRootDepth;
        _storage = storage;
        _acl = acl;
        _searchService = searchService;
        _shareId = shareId;
        _versionService = versionService;
        _ownershipService = ownershipService;
        _cloudSyncPathUpdater = cloudSyncPathUpdater;
        _cloudSyncOperations = cloudSyncOperations;
        _changeLog = changeLog;
        _logger = logger ?? NullLogger<FileService>.Instance;
    }

    // ------------------ Directory Listing ------------------

    public async Task<List<FileMetadata>> ListAsync(string directoryPath, UserContext user)
    {
        var normalizedDir = ShareRelativePath.Normalize(directoryPath);

        if (!ShareEntryPolicy.IsVisibleInFileBrowser(normalizedDir, _recycleRootDepth))
            throw new UnauthorizedAccessException(
                $"Internal share path '{normalizedDir}' is not browser-visible.");

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

        var browserItems = items
            .Select(item => (
                Item: item,
                Path: ShareRelativePath.Combine(normalizedDir, item.Name)))
            .Where(entry =>
                ShareEntryPolicy.IsVisibleInFileBrowser(entry.Path, _recycleRootDepth))
            .ToList();

        var itemsToCheck = browserItems
            .Select(entry => (entry.Path, entry.Item.IsDirectory))
            .ToList();

        var accessMap = await _acl.HasAccessBatchAsync(
            user, _shareId, itemsToCheck, FilePermission.ListReadData);

        var visible = new List<FileMetadata>(browserItems.Count);
        foreach (var entry in browserItems)
        {
            if (accessMap.TryGetValue(entry.Path, out var allowed) && allowed)
                visible.Add(entry.Item);
        }


        return visible;
    }

    /// <summary>
    /// The same rules as <see cref="ListAsync"/> applied to the parent, in the same order, but
    /// for one entry: a constant number of storage and ACL calls instead of stat-ing and
    /// authorizing every sibling (a WebDAV PUT into a folder of n files used to cost O(n)).
    /// </summary>
    public async Task<FileMetadata?> FindEntryAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        if (normalized.Length == 0)
            return await GetMetadataAsync(string.Empty, user);

        var parent = ShareRelativePath.GetParent(normalized);
        if (!ShareEntryPolicy.IsVisibleInFileBrowser(parent, _recycleRootDepth))
            throw new UnauthorizedAccessException(
                $"Internal share path '{parent}' is not browser-visible.");

        if (!await _acl.HasAccessAsync(user, _shareId, parent, true, FilePermission.ListReadData))
            throw new UnauthorizedAccessException($"List denied for '{parent}'");

        if (!await _storage.ExistsAsync(parent) || !await _storage.IsDirectoryAsync(parent))
            return null;

        if (!ShareEntryPolicy.IsVisibleInFileBrowser(normalized, _recycleRootDepth)
            || !await _storage.ExistsAsync(normalized))
            return null;

        FileMetadata entry;
        try { entry = await _storage.GetMetadataAsync(normalized); }
        catch (FileNotFoundException) { return null; } // deleted meanwhile

        var access = await _acl.HasAccessBatchAsync(
            user, _shareId, [(normalized, entry.IsDirectory)], FilePermission.ListReadData);
        return access.TryGetValue(normalized, out var allowed) && allowed ? entry : null;
    }

    // ------------------ Full Operations ------------------

    public Task<Stream> ReadFileAsync(string path, UserContext user)
        => ReadFileAsync(path, user, preserveAccessTime: false);

    public async Task<Stream> ReadFileAsync(string path, UserContext user, bool preserveAccessTime)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);

        await EnsureAccessAsync(user, normalized, isDir, FilePermission.ListReadData);

        return preserveAccessTime
            ? await _storage.ReadAsync(normalized, preserveAccessTime: true)
            : await _storage.ReadAsync(normalized);
    }

    public Task SetModifiedAtAsync(string path, UserContext user, DateTime time)
        => SetModifiedAtAsync(path, user, time, createdAtUtc: null);

    public Task SetModifiedAtAsync(string path, UserContext user, DateTime? modifiedAtUtc, DateTime? createdAtUtc)
        => SetTimesAsync(path, user, new FileTimes(createdAtUtc, modifiedAtUtc, null));

    public async Task SetTimesAsync(string path, UserContext user, FileTimes times)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);

        // Directories are stamped too (sync keeps folder times; WebDAV PROPPATCH on a collection).
        await EnsureAccessAsync(user, normalized, isDir, FilePermission.CreateWriteData);

        if (!await _storage.ExistsAsync(normalized))
            throw new FileNotFoundException($"'{normalized}' does not exist.", normalized);

        // Order matters: stamping the mtime also sets the atime, so an explicit access time goes last.
        if (times.Created is { } created)
            await _storage.SetCreatedDateAsync(normalized, created);
        if (times.LastWritten is { } modified)
            await _storage.SetModifiedDateAsync(normalized, modified);
        if (times.LastAccessed is { } accessed)
            await _storage.SetAccessedDateAsync(normalized, accessed);
        _logger.LogDebug(LogEvents.FileTimesApplied, LogMessages.FileTimesApplied,
            normalized, times.Created, times.LastWritten, times.LastAccessed);

        // The mtime is part of the item tag: log it so feed-based clients rebuild a current tag.
        await AppendFileUpsertAsync(FileChangeType.Modified, normalized);
    }

    public Task WriteFileAsync(string path, Stream data, UserContext user, CancellationToken cancellationToken = default)
        => WriteFileAsync(path, data, user, modifiedAtUtc: null, cancellationToken);

    public Task WriteFileAsync(string path, Stream data, UserContext user, DateTime? modifiedAtUtc, CancellationToken cancellationToken = default)
        => WriteFileAsync(path, data, user, modifiedAtUtc, createdAtUtc: null, cancellationToken);

    public Task WriteFileAsync(string path, Stream data, UserContext user, DateTime? modifiedAtUtc, DateTime? createdAtUtc, CancellationToken cancellationToken = default)
        => WriteFileAsync(path, data, user, new FileTimes(createdAtUtc, modifiedAtUtc, null), cancellationToken);

    public async Task WriteFileAsync(string path, Stream data, UserContext user, FileTimes sourceTimes, CancellationToken cancellationToken = default)
    {
        var (createdAtUtc, modifiedAtUtc, accessedAtUtc) = sourceTimes;
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);

        await EnsureAccessAsync(user, normalized, isDir, FilePermission.CreateWriteData);

        // Distinguish a fresh create from an overwrite so ownership is stamped on the
        // creator, never reassigned to whoever later overwrites the file.
        var existedBefore = await _storage.ExistsAsync(normalized);

        // An overwrite only adopts a source mtime that moves the file's time forward. The
        // stored time then never goes backwards (other devices decide "newest wins" on it),
        // and every overwrite changes the size:mtime item tag, so If-Match and listing ETags
        // still detect it.
        var previous = (modifiedAtUtc is not null || createdAtUtc is not null || accessedAtUtc is not null) && existedBefore
            ? await _storage.GetMetadataAsync(normalized)
            : null;
        DateTime? previousModifiedAt = previous?.ModifiedAt.ToUniversalTime();

        await _storage.WriteAsync(normalized, data, cancellationToken);

        // Stamp the source mtime before the change-log append, so the feed entry (which stats
        // the file) carries the preserved time instead of the write time.
        // The times actually stored (null = kept), for the FileTimesApplied trace below.
        DateTime? appliedCreated = null, appliedModified = null, appliedAccessed = null;
        if (modifiedAtUtc is { } sourceModifiedAt
            && (previousModifiedAt is null || sourceModifiedAt.ToUniversalTime() > previousModifiedAt))
        {
            await _storage.SetModifiedDateAsync(normalized, sourceModifiedAt);
            appliedModified = sourceModifiedAt;
        }

        // The creation time only ever moves back: an older source time means the item existed
        // earlier on the other side, so both sides converge on the earliest one.
        if (createdAtUtc is { } sourceCreatedAt
            && (previous is null || sourceCreatedAt.ToUniversalTime() < previous.CreatedAt.ToUniversalTime()))
        {
            await _storage.SetCreatedDateAsync(normalized, sourceCreatedAt);
            appliedCreated = sourceCreatedAt;
        }

        // The access time goes last (the mtime stamp above also sets it) and, like the mtime,
        // only moves forward on an overwrite.
        if (accessedAtUtc is { } sourceAccessedAt)
        {
            var previousAccessedAt = previous?.LastAccessedAt?.ToUniversalTime();
            appliedAccessed = previousAccessedAt > sourceAccessedAt.ToUniversalTime()
                ? previousAccessedAt.Value
                : sourceAccessedAt;
            await _storage.SetAccessedDateAsync(normalized, appliedAccessed.Value);
        }

        if (createdAtUtc is not null || modifiedAtUtc is not null || accessedAtUtc is not null)
            _logger.LogDebug(LogEvents.FileTimesApplied, LogMessages.FileTimesApplied,
                normalized, appliedCreated, appliedModified, appliedAccessed);

        if (!existedBefore)
            await RecordOwnerAsync(normalized, isDirectory: false, user);

        await AppendFileUpsertAsync(
            existedBefore ? FileChangeType.Modified : FileChangeType.Created, normalized);

        // Versioning: snapshot the freshly-written content so web uploads/overwrites
        // build the same version history that SMB writes do (FileSession.DisposeAsync).
        // Content-addressable dedup skips this when the content is unchanged. A
        // versioning failure must never fail the upload itself.
        if (!isDir && _versionService != null)
        {
            try
            {
                await using var written = await _storage.ReadAsync(normalized, preserveAccessTime: true);
                await _versionService.CreateVersionAsync(
                    _shareId, normalized, written, user.User.Id.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(LogEvents.FileWriteVersionFailed, ex, LogMessages.FileWriteVersionFailed, normalized);
            }
        }

        // Search indexing is derived and rebuildable. Open the persisted file inside
        // the observed background operation so upload completion does not wait for
        // Elasticsearch reachability checks or indexing.
        var absolutePath = ToAbsolutePath(normalized);
        StartSearchSideEffect(
            () => IndexStoredFileAsync(normalized, absolutePath),
            "file create",
            normalized);
    }

    public string ToAbsolutePath(string path)
    {
        var normalized = ShareRelativePath.Normalize(path);
        return _storage.ToAbsolutePath(normalized);
    }
    
    public async Task ArchiveAsync(List<string> sourcePaths, string targetPath, string format, UserContext user)
    {
        var normalizedTarget = ShareRelativePath.Normalize(targetPath);
        EnsureUserWriteTargetAllowed(normalizedTarget);

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

        // Archiving produces a single new archive file at the target.
        await AppendFileUpsertAsync(FileChangeType.Created, normalizedTarget);
    }
    
    public async Task UnzipAsync(string zipPath, string targetPath, UserContext user)
    {
        var normalizedZip = ShareRelativePath.Normalize(zipPath);
        var normalizedTarget = ShareRelativePath.Normalize(targetPath);
        EnsureUserWriteTargetAllowed(normalizedTarget);

        // Check read permission on the zip
        await EnsureAccessAsync(user, normalizedZip, false, FilePermission.ListReadData);

        // Check write permission on the target directory
        var parentDir = ShareRelativePath.GetParent(normalizedTarget);
        await EnsureAccessAsync(user, parentDir, true, FilePermission.CreateWriteData);

        await _storage.UnzipAsync(normalizedZip, normalizedTarget);

        // Extraction can create an unbounded number of items under the target — record it as one
        // coarse subtree change; the client reconciles that subtree with a scoped delta.
        await AppendChangeAsync(FileChangeType.SubtreeChanged, normalizedTarget, isDirectory: true);
    }
    

    public async Task CreateFileAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        EnsureUserWriteTargetAllowed(normalized);
        var parentPath = ShareRelativePath.GetParent(normalized);

        await EnsureAccessAsync(user, parentPath, true, FilePermission.CreateWriteData);

        await _storage.WriteAsync(normalized, Stream.Null);
        await RecordOwnerAsync(normalized, isDirectory: false, user);
        await AppendChangeAsync(
            FileChangeType.Created, normalized, isDirectory: false,
            size: 0, modifiedAtUtc: DateTime.UtcNow);
    }

    public async Task CreateDirectoryAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        EnsureUserWriteTargetAllowed(normalized);
        var parentPath = ShareRelativePath.GetParent(normalized);

        await EnsureAccessAsync(user, parentPath, true, FilePermission.CreateWriteData);

        await _storage.CreateDirectory(normalized);
        await RecordOwnerAsync(normalized, isDirectory: true, user);
        await AppendChangeAsync(FileChangeType.Created, normalized, isDirectory: true);
        var absolutePath = ToAbsolutePath(normalized);
        StartSearchSideEffect(
            () => _searchService!.onDirectoryCreated(absolutePath),
            "directory create",
            normalized);
    }

    public async Task DeleteFileAsync(string path, UserContext user, bool isRecycleEnabled)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);

        await EnsureAccessAsync(user, normalized, isDir, FilePermission.Delete);

        var isAlreadyInRecycleBin =
            ShareEntryPolicy.IsRecycleBinPath(normalized, _recycleRootDepth);

        var absolutePath = ToAbsolutePath(normalized);
        
        if (isRecycleEnabled && !isAlreadyInRecycleBin)
        {
            var recyclePath = ShareEntryPolicy.GetRecyclePath(
                normalized, _recycleRootDepth);
            // MoveAsync may append a timestamp suffix on a name collision in the
            // recycle bin — align the ACL with the path that actually landed on disk.
            var actualRecyclePath = await _storage.MoveAsync(normalized, recyclePath);
            var actualRecycleAbsolutePath = ToAbsolutePath(actualRecyclePath);
            await ApplyRenameSideEffectsAsync(
                normalized, actualRecyclePath, isDir,
                absolutePath, actualRecycleAbsolutePath, bestEffort: false,
                includeSearch: false, updateCloudSyncPaths: false);
            StartSearchSideEffect(
                () => isDir
                    ? _searchService!.onDirectoryRenamed(absolutePath, actualRecycleAbsolutePath)
                    : _searchService!.onFileRenamed(absolutePath, actualRecycleAbsolutePath),
                "recycle move",
                normalized);
        }
        else
        {
            await _storage.DeleteAsync(normalized);
            await ApplyDeleteSideEffectsAsync(
                normalized, isDir, absolutePath, bestEffort: false,
                includeSearch: false);
            StartSearchSideEffect(
                () => isDir
                    ? _searchService!.onDirectoryDeleted(absolutePath)
                    : _searchService!.onFileDeleted(absolutePath),
                "delete",
                normalized);
        }
    }

    public async Task<FileMetadata> GetMetadataAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        FileMetadata meta;
        try
        {
            meta = await _storage.GetMetadataAsync(normalized);
        }
        catch (FileNotFoundException)
        {
            // Authorize before revealing that the item does not exist (no existence oracle).
            await EnsureAccessAsync(user, normalized, false, FilePermission.ListReadData);
            throw;
        }

        await EnsureAccessAsync(user, normalized, meta.IsDirectory, FilePermission.ListReadData);

        return meta;
    }

    public async Task RenameAsync(string oldPath, string newPath, UserContext user)
    {
        var oldNormalized = ShareRelativePath.Normalize(oldPath);
        var newNormalized = ShareRelativePath.Normalize(newPath);
        await using var operationLease =
            await BeginPathMutationOrThrowAsync(oldNormalized, newNormalized);
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

        if (IsInternalBelowRecycleRoot(normalized) ||
            (wantsWrite || !exists && wantsCreate) &&
            ShareEntryPolicy.IsReservedForUserWrites(normalized, _recycleRootDepth))
            throw new UnauthorizedAccessException(
                $"Share path '{normalized}' uses a reserved Kaimo namespace.");

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
        {
            await RecordOwnerAsync(normalized, storageHandle.IsDirectory, user);
            // Record the creation now (covers an empty file that is opened and closed without a
            // write); a subsequent content write is recorded as Modified at FileSession close.
            await AppendChangeAsync(
                FileChangeType.Created, normalized, storageHandle.IsDirectory);
        }

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

}
