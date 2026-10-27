using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Core.Services.File;

public partial class FileVersionService
{
    // ------------------ Reconciliation ------------------

    /// <summary>
    /// Brings the blob files in line with the database: copies every referenced blob
    /// into the pool of its share (draining the legacy application-data store and
    /// following shares that moved to another pool) and deletes copies no share of
    /// that pool needs. Safe to run repeatedly; a no-op without a locator.
    /// </summary>
    public async Task ReconcileStorageAsync(CancellationToken cancellationToken = default)
    {
        if (_locator is null) return;

        var references = await _versionRepo.GetAllBlobReferencesAsync(cancellationToken);
        var rootByShare = new Dictionary<Guid, string?>();
        foreach (var shareId in references.Select(r => r.ShareId).Distinct())
            rootByShare[shareId] = await _locator.GetRootAsync(shareId);

        // Every (pool root, blob) pair that must exist on disk.
        var wanted = references
            .Where(r => rootByShare[r.ShareId] is not null)
            .Select(r => (Root: rootByShare[r.ShareId]!, r.StoragePath))
            .ToHashSet();

        int copied = 0, missing = 0, deleted = 0;
        var fullRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (root, storagePath) in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fullRoots.Contains(root) || System.IO.File.Exists(Path.Combine(root, storagePath)))
                continue;

            var source = FindBlob(null, storagePath);
            if (source is null)
            {
                missing++;
                continue;
            }

            Directory.CreateDirectory(root);
            if (!HasRoomFor(root, new FileInfo(source.Value.FullPath).Length))
            {
                // The source copy stays where it is and is retried on the next run.
                _logger.LogWarning(
                    "Version storage {Root} is low on free space; moving versions into it is postponed.", root);
                fullRoots.Add(root);
                continue;
            }

            if (await CopyBlobAsync(source.Value.FullPath, Path.Combine(root, storagePath), cancellationToken))
                copied++;
        }

        var now = DateTime.UtcNow;
        foreach (var root in KnownRoots.Where(Directory.Exists).ToList())
        {
            bool isLegacy = SamePath(root, _versionStorageRoot);
            foreach (var file in Directory.EnumerateFiles(root, "*.bin.gz", SearchOption.AllDirectories).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var storagePath = Path.GetRelativePath(root, file);
                // The legacy store receives no new blobs, so it needs no age guard.
                if (!isLegacy && (wanted.Contains((root, storagePath))
                                  || System.IO.File.GetLastWriteTimeUtc(file) > now - ReconcileMinBlobAge))
                    continue;
                if (await DeleteBlobIfUnneededAsync(root, storagePath))
                    deleted++;
            }

            DeleteStaleTempFiles(root, now - StaleTempAge);
            RemoveEmptyDirectories(root, includeRoot: isLegacy);
        }

        if (copied > 0 || deleted > 0 || missing > 0)
            _logger.LogInformation(
                "Version storage reconciled: {Copied} blobs moved into their pool, {Deleted} unneeded copies removed, {Missing} referenced blobs missing.",
                copied, deleted, missing);
    }

    private async Task<bool> CopyBlobAsync(string source, string target, CancellationToken cancellationToken)
    {
        var blobLock = GetBlobLock(Path.GetFullPath(target));
        await blobLock.WaitAsync(cancellationToken);
        var directory = Path.GetDirectoryName(target)!;
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (System.IO.File.Exists(target))
                return false;

            Directory.CreateDirectory(directory);
            await using (var input = new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken);
                output.Flush(flushToDisk: true);
            }

            System.IO.File.Move(tempPath, target, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to move version blob {Source} to {Target}", source, target);
            return false;
        }
        finally
        {
            try { System.IO.File.Delete(tempPath); }
            catch { /* best effort */ }
            blobLock.Release();
        }
    }

    /// <summary>
    /// Removes write/read temp files a crashed process left behind. A blob parked by an
    /// interrupted garbage collection may still be referenced: it goes back instead (parking
    /// keeps the blob's old timestamp, so it is "stale" right away).
    /// </summary>
    private void DeleteStaleTempFiles(string root, DateTime olderThanUtc)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).ToList())
        {
            if (OriginalOfParkedBlob(file) is { } original)
            {
                var blobLock = GetBlobLock(Path.GetFullPath(original));
                if (!blobLock.Wait(0)) continue; // collection in progress in this process
                try { RestoreOrDeleteParkedBlob(file, original); }
                finally { blobLock.Release(); }
                continue;
            }

            try
            {
                if (System.IO.File.GetLastWriteTimeUtc(file) < olderThanUtc)
                    System.IO.File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not delete stale version temp file {File}", file);
            }
        }
    }

    private static void RemoveEmptyDirectories(string root, bool includeRoot)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length).ToList())
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }

            if (includeRoot && !Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cosmetic cleanup only; retried on the next run.
        }
    }

    private static SemaphoreSlim GetBlobLock(string fullPath)
    {
        var hash = (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(fullPath);
        return BlobLocks[hash % (uint)BlobLocks.Length];
    }

    private static void RemoveEmptyParentDirectories(string? directory, string rootWithSeparator)
    {
        var root = rootWithSeparator.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrEmpty(directory)
               && !string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)
               && Directory.Exists(directory)
               && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    /// <summary>
    /// Truncates a DateTime to second precision — @GMT- format has second precision.
    /// </summary>
    private static DateTime TruncateToSeconds(DateTime dt)
    {
        return new DateTime(dt.Year, dt.Month, dt.Day,
            dt.Hour, dt.Minute, dt.Second, dt.Kind);
    }
}
