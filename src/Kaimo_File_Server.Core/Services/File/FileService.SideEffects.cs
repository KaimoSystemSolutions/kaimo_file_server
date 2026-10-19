using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Core.Services.File;

public partial class FileService
{
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

    /// <summary>
    /// Appends one entry to the per-share change log that backs the client sync <c>change_seq</c>
    /// feed. Best-effort: a failure must never fail the underlying file operation (a lost row is
    /// recovered by the client's periodic full <c>delta</c> reconcile). No-op when no change log is
    /// configured.
    /// </summary>
    private async Task AppendChangeAsync(
        FileChangeType type, string relativePath, bool isDirectory,
        string? oldRelativePath = null, long? size = null, DateTime? modifiedAtUtc = null)
    {
        if (_changeLog is null) return;
        try
        {
            await _changeLog.AppendAsync(
                _shareId, type, relativePath, isDirectory, oldRelativePath, size, modifiedAtUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Change-log append ({ChangeType}) failed for {Path}", type, relativePath);
        }
    }

    /// <summary>
    /// Appends a create/modify entry for a file, enriching it with the just-written item's size and
    /// modified time so a client can rebuild its item tag without a metadata round trip. The stat is
    /// optional enrichment — its failure never blocks the entry.
    /// </summary>
    private async Task AppendFileUpsertAsync(FileChangeType type, string relativePath)
    {
        if (_changeLog is null) return;

        long? size = null;
        DateTime? modifiedAt = null;
        var isDirectory = false;
        try
        {
            var meta = await _storage.GetMetadataAsync(relativePath);
            // A directory is logged as such (SetModifiedAtAsync stamps folders too), never as a file.
            isDirectory = meta.IsDirectory;
            size = isDirectory ? null : meta.Size;
            modifiedAt = meta.ModifiedAt;
        }
        catch { /* size/mtime are optional enrichment for the change feed */ }

        await AppendChangeAsync(
            type, relativePath, isDirectory: isDirectory, size: size, modifiedAtUtc: modifiedAt);
    }

    private async Task ApplyDeleteSideEffectsAsync(
        string relativePath, bool isDirectory, string absolutePath, bool bestEffort,
        bool includeSearch = true)
    {
        var failures = new List<Exception>();

        try { await _acl.DeleteAclAsync(_shareId, relativePath); }
        catch (Exception ex) { failures.Add(ex); }

        if (_versionService != null)
        {
            try { await _versionService.DeletePathAsync(_shareId, relativePath); }
            catch (Exception ex) { failures.Add(ex); }
        }

        if (includeSearch && _searchService != null)
        {
            try
            {
                if (isDirectory) await _searchService.onDirectoryDeleted(absolutePath);
                else await _searchService.onFileDeleted(absolutePath);
            }
            catch (Exception ex) { failures.Add(ex); }
        }

        // Record the deletion for the client change feed. The on-disk delete already happened,
        // so log it regardless of whether a cleanup side-effect above failed.
        await AppendChangeAsync(FileChangeType.Deleted, relativePath, isDirectory);

        if (failures.Count == 0) return;
        var aggregate = new AggregateException(
            $"One or more delete side effects failed for '{relativePath}'.", failures);
        if (!bestEffort) throw aggregate;
        _logger.LogWarning(aggregate, "Delete lifecycle cleanup failed for {Path}", relativePath);
    }

    private async Task ApplyRenameSideEffectsAsync(
        string oldRelativePath, string newRelativePath, bool isDirectory,
        string oldAbsolutePath, string newAbsolutePath, bool bestEffort,
        Guid? sambaLifecycleEventId = null, bool includeSearch = true,
        bool updateCloudSyncPaths = true)
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

        if (isDirectory && updateCloudSyncPaths &&
            _cloudSyncPathUpdater != null)
        {
            try
            {
                await _cloudSyncPathUpdater.RenamePathAsync(
                    _shareId, oldRelativePath, newRelativePath);
            }
            catch (Exception ex) { failures.Add(ex); }
        }

        if (includeSearch && _searchService != null)
        {
            try
            {
                if (isDirectory) await _searchService.onDirectoryRenamed(oldAbsolutePath, newAbsolutePath);
                else await _searchService.onFileRenamed(oldAbsolutePath, newAbsolutePath);
            }
            catch (Exception ex) { failures.Add(ex); }
        }

        // Record the rename for the client change feed (covers moves to the recycle bin too). The
        // on-disk move already happened, so log it regardless of a failed cleanup side-effect.
        await AppendChangeAsync(
            FileChangeType.Renamed, newRelativePath, isDirectory, oldRelativePath);

        if (failures.Count == 0) return;
        var aggregate = new AggregateException(
            $"One or more rename side effects failed for '{oldRelativePath}' -> '{newRelativePath}'.",
            failures);
        if (!bestEffort) throw aggregate;
        _logger.LogWarning(aggregate, "Rename lifecycle cleanup failed for {OldPath} -> {NewPath}",
            oldRelativePath, newRelativePath);
    }

    private async Task<ICloudSyncOperationLease?> BeginPathMutationOrThrowAsync(
        string oldRelativePath,
        string newRelativePath,
        CancellationToken cancellationToken = default)
    {
        if (_cloudSyncOperations is null)
            return null;
        var lease = await _cloudSyncOperations.TryBeginPathMutationAsync(
            _shareId, oldRelativePath, newRelativePath, cancellationToken);
        if (lease is not null)
            return lease;

        throw new CloudSyncOperationConflictException(
            $"Cannot rename '{oldRelativePath}' to '{newRelativePath}' " +
            "while an overlapping cloud sync is running.");
    }

    /// <summary>
    /// Starts a rebuildable search-index update without extending the user-facing
    /// filesystem operation. Failures are observed and logged; authoritative ACL
    /// and version metadata are still completed synchronously by the caller.
    /// </summary>
    private void StartSearchSideEffect(
        Func<Task> action, string operation, string path)
    {
        if (_searchService is null) return;
        _ = ObserveSearchSideEffectAsync(action, operation, path);
    }

    private async Task ObserveSearchSideEffectAsync(
        Func<Task> action, string operation, string path)
    {
        await _searchSideEffectLock.WaitAsync();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Background search-index {Operation} failed for {Path}",
                operation,
                path);
        }
        finally
        {
            _searchSideEffectLock.Release();
        }
    }

    private async Task IndexStoredFileAsync(
        string relativePath, string absolutePath)
    {
        var fileData = _storage.ReadAsync(relativePath, preserveAccessTime: true);
        try
        {
            await OnFileCreated(absolutePath, fileData);
        }
        catch
        {
            // The search implementation may fail before it takes ownership of the
            // stream. Dispose it here as a safe, idempotent fallback.
            try { await DrainStreamAsync(fileData); }
            catch { /* preserve the indexing failure as the useful diagnostic */ }
            throw;
        }
    }
}
