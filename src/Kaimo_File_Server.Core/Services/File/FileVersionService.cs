using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Kaimo_File_Server.Core.Services.File;

/// <summary>
/// Core implementation of file versioning logic.
///
/// Storage layout for version blobs (content-addressable, compressed):
///   {versionStorageRoot}/AB/CD/ABCDEF1234567890...sha256.bin.gz
///
/// The first 2 bytes of the hash form subdirectories to avoid
/// having millions of files in one folder.
///
/// Deduplication strategy:
///   - SHA-256 hash of the UNCOMPRESSED content
///   - If the latest version of a file has the same hash → skip (no new version)
///   - If a blob with that hash already exists on disk → reuse (no new write)
///   - Blobs are stored gzip-compressed to save disk space
///
/// Path convention:
///   All filePath parameters are share-relative (normalized via ShareRelativePath).
///   The ShareId is NOT part of the path — it is carried alongside every call and
///   stored on each version so histories stay isolated between shares that happen
///   to contain a file at the same relative path.
/// </summary>
public class FileVersionService : IFileVersionService
{
    private const long InMemoryReadLimitBytes = 8L * 1024 * 1024;
    private static readonly SemaphoreSlim[] BlobLocks = Enumerable.Range(0, 64)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();
    private readonly IFileVersionRepository _versionRepo;
    private readonly string _versionStorageRoot;
    private readonly int _defaultMaxVersions;
    private readonly TimeSpan? _defaultMaxAge;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FileVersionService> _logger;

    public FileVersionService(
        IFileVersionRepository versionRepo,
        string versionStorageRoot,
        int defaultMaxVersions = 64,
        TimeSpan? defaultMaxAge = null,
        TimeProvider? timeProvider = null,
        ILogger<FileVersionService>? logger = null)
    {
        _versionRepo = versionRepo;
        _versionStorageRoot = versionStorageRoot;
        _defaultMaxVersions = defaultMaxVersions;
        _defaultMaxAge = defaultMaxAge;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<FileVersionService>.Instance;

        Directory.CreateDirectory(_versionStorageRoot);
    }

    public async Task<FileVersion?> CreateVersionAsync(
        Guid shareId, string filePath, Stream content, string? userId = null)
    {
        var normalizedPath = ShareRelativePath.Normalize(filePath);

        // 1. Hash the content
        content.Position = 0;
        var hash = await ComputeHashAsync(content);
        content.Position = 0;

        var contentSize = content.Length;
        var versionAlreadyExists =
            await _versionRepo.ExistsWithHashAsync(shareId, normalizedPath, hash);

        // 2. Store the blob compressed (content-addressable). Even when the
        // version record already exists, validate/repair its physical blob before
        // returning so a crash-left partial file cannot become permanent.
        var blobRelativePath = HashToPath(hash);
        var blobFullPath = Path.Combine(_versionStorageRoot, blobRelativePath);
        long compressedSize;
        var createdBlob = false;
        FileVersion? version = null;
        int versionNumber = 0;

        var blobLock = GetBlobLock(blobFullPath);
        await blobLock.WaitAsync();
        try
        {
            var blobExisted = System.IO.File.Exists(blobFullPath);
            var blobIsValid = blobExisted
                && await ValidateBlobAsync(blobFullPath, hash, contentSize, Stream.Null);

            if (!blobIsValid)
            {
                if (blobExisted)
                    _logger.LogWarning(
                        LogEvents.FileVersionBlobCorruptReplaced,
                        LogMessages.FileVersionBlobCorruptReplaced, blobRelativePath);

                content.Position = 0;
                compressedSize = await WriteValidatedBlobAsync(
                    blobFullPath, content, hash, contentSize);
                createdBlob = !blobExisted;
            }
            else
            {
                compressedSize = new FileInfo(blobFullPath).Length;
            }

            content.Position = 0;

            if (versionAlreadyExists)
                return null;

            // 3. Create version record while holding the blob stripe. A concurrent
            // delete cannot reclaim the blob between the existence check and insert.
            versionNumber = await _versionRepo.GetMaxVersionNumberAsync(shareId, normalizedPath) + 1;
            var now = TruncateToSeconds(_timeProvider.GetUtcNow().UtcDateTime);

            // If a version at this exact second already exists, bump to next free second
            while (await _versionRepo.GetVersionAsync(shareId, normalizedPath, now) != null)
                now = now.AddSeconds(1);

            version = new FileVersion(
                shareId: shareId,
                filePath: normalizedPath,
                snapshotTimestampUtc: now,
                storagePath: blobRelativePath,
                contentHash: hash,
                size: contentSize,
                createdBy: userId,
                versionNumber: versionNumber);

            await _versionRepo.CreateAsync(version);
        }
        catch
        {
            // The filesystem write precedes the DB insert. If the insert fails,
            // reclaim the blob unless another version already references it.
            if (createdBlob && !await _versionRepo.IsStoragePathReferencedAsync(blobRelativePath))
                System.IO.File.Delete(blobFullPath);
            throw;
        }
        finally
        {
            blobLock.Release();
        }

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug(LogEvents.FileVersionCreated, LogMessages.FileVersionCreated,
                versionNumber, normalizedPath, contentSize, compressedSize,
                ((double)compressedSize / Math.Max(contentSize, 1)).ToString("P0"));

        // 4. Enforce retention
        await ApplyRetentionAsync(shareId, normalizedPath, _defaultMaxVersions, _defaultMaxAge);

        return version!;
    }

    public async Task<Stream> ReadVersionAsync(
        Guid shareId, string filePath, DateTime snapshotTimestampUtc)
        => await ReadVersionAsync(
            shareId, filePath, snapshotTimestampUtc, CancellationToken.None);

    public async Task<Stream> ReadVersionAsync(
        Guid shareId, string filePath, DateTime snapshotTimestampUtc,
        CancellationToken cancellationToken)
    {
        var normalizedPath = ShareRelativePath.Normalize(filePath);

        var version = await _versionRepo.GetVersionAsync(shareId, normalizedPath, snapshotTimestampUtc);
        if (version == null)
            throw new FileNotFoundException(
                $"No version found for '{normalizedPath}' at {snapshotTimestampUtc:O}");

        var blobFullPath = Path.Combine(_versionStorageRoot, version.StoragePath);
        if (!System.IO.File.Exists(blobFullPath))
            throw new FileNotFoundException(
                $"Version blob missing: {version.StoragePath}");

        // Keep small snapshots fast in memory, but spill large snapshots to a
        // delete-on-close seekable file so concurrent downloads cannot exhaust RAM.
        Stream output;
        if (version.Size <= InMemoryReadLimitBytes)
        {
            output = new MemoryStream((int)Math.Max(version.Size, 0));
        }
        else
        {
            var readCache = Path.Combine(_versionStorageRoot, ".read-cache");
            Directory.CreateDirectory(readCache);
            output = new FileStream(
                Path.Combine(readCache, Guid.NewGuid().ToString("N") + ".tmp"),
                FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        }

        try
        {
            await ValidateBlobAsync(
                blobFullPath, version.ContentHash, version.Size, output,
                cancellationToken, throwOnInvalid: true);
            output.Position = 0;
            return output;
        }
        catch
        {
            await output.DisposeAsync();
            throw;
        }
    }

    public async Task<List<FileVersion>> GetVersionsAsync(Guid shareId, string filePath)
    {
        return await _versionRepo.GetVersionsAsync(shareId, ShareRelativePath.Normalize(filePath));
    }

    public async Task<List<DateTime>> GetSnapshotTimestampsAsync(Guid shareId, string pathPrefix = "")
    {
        return await _versionRepo.GetAllSnapshotTimestampsAsync(shareId, FolderPrefix(pathPrefix));
    }

    public async Task<List<FileVersion>> GetFolderSnapshotAsync(Guid shareId, string folderPath, DateTime asOfUtc)
        => await GetFolderSnapshotAsync(
            shareId, folderPath, asOfUtc, CancellationToken.None);

    public async Task<List<FileVersion>> GetFolderSnapshotAsync(
        Guid shareId, string folderPath, DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        return await _versionRepo.GetLatestVersionsUnderPrefixAsync(
            shareId, FolderPrefix(folderPath), asOfUtc, cancellationToken);
    }

    /// <summary>
    /// Normalizes a folder path into a prefix that matches only files inside
    /// that folder — a trailing slash prevents "foo" from matching "foobar/…".
    /// The share root ("") stays empty so it matches every file.
    /// </summary>
    private static string FolderPrefix(string folderPath)
    {
        var normalized = ShareRelativePath.Normalize(folderPath);
        return normalized.Length == 0 ? "" : normalized + "/";
    }

    public async Task<FileVersion?> GetVersionAtAsync(
        Guid shareId, string filePath, DateTime snapshotTimestampUtc)
    {
        return await _versionRepo.GetVersionAsync(
            shareId, ShareRelativePath.Normalize(filePath), snapshotTimestampUtc);
    }

    public async Task<int> ApplyRetentionAsync(
        Guid shareId, string filePath, int? maxVersions = null, TimeSpan? maxAge = null)
    {
        var normalizedPath = ShareRelativePath.Normalize(filePath);
        var removed = new List<FileVersion>();

        var effectiveMax = maxVersions ?? _defaultMaxVersions;
        var effectiveAge = maxAge ?? _defaultMaxAge;

        if (effectiveMax > 0)
            removed.AddRange(await _versionRepo.TrimToMaxVersionsAsync(
                shareId, normalizedPath, effectiveMax));

        if (effectiveAge.HasValue)
        {
            var cutoff = _timeProvider.GetUtcNow().UtcDateTime - effectiveAge.Value;
            removed.AddRange(await _versionRepo.DeleteOlderThanAsync(
                shareId, normalizedPath, cutoff));
        }

        await DeleteUnreferencedBlobsAsync(removed);
        return removed.Count;
    }

    public async Task RenamePathAsync(
        Guid shareId,
        string oldPath,
        string newPath,
        Guid? sambaLifecycleEventId = null)
    {
        var oldNormalized = ShareRelativePath.Normalize(oldPath);
        var newNormalized = ShareRelativePath.Normalize(newPath);
        if (string.Equals(oldNormalized, newNormalized, StringComparison.Ordinal)) return;

        var displaced = await _versionRepo.RenamePathAsync(
            shareId, oldNormalized, newNormalized, sambaLifecycleEventId);
        await DeleteUnreferencedBlobsAsync(displaced);
    }

    public async Task<int> DeletePathAsync(Guid shareId, string path)
    {
        var removed = await _versionRepo.DeletePathAsync(
            shareId, ShareRelativePath.Normalize(path));
        await DeleteUnreferencedBlobsAsync(removed);
        return removed.Count;
    }

    public async Task<int> DeleteShareAsync(Guid shareId)
    {
        var removed = await _versionRepo.DeleteShareAsync(shareId);
        await DeleteUnreferencedBlobsAsync(removed);
        return removed.Count;
    }

    // ------------------ Helpers ------------------

    private static async Task<string> ComputeHashAsync(Stream stream)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(stream);
        return Convert.ToHexString(hashBytes);
    }

    private static async Task<long> WriteValidatedBlobAsync(
        string blobFullPath, Stream content, string expectedHash, long expectedSize)
    {
        var directory = Path.GetDirectoryName(blobFullPath)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(blobFullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var fs = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using (var gzip = new GZipStream(
                    fs, CompressionLevel.Optimal, leaveOpen: true))
                {
                    await content.CopyToAsync(gzip);
                }

                await fs.FlushAsync();
                fs.Flush(flushToDisk: true);
            }

            await ValidateBlobAsync(
                tempPath, expectedHash, expectedSize, Stream.Null,
                CancellationToken.None, throwOnInvalid: true);

            var compressedSize = new FileInfo(tempPath).Length;
            System.IO.File.Move(tempPath, blobFullPath, overwrite: true);
            return compressedSize;
        }
        finally
        {
            try { System.IO.File.Delete(tempPath); }
            catch { /* preserve the compression/validation exception */ }
        }
    }

    private static async Task<bool> ValidateBlobAsync(
        string blobFullPath, string expectedHash, long expectedSize, Stream output,
        CancellationToken cancellationToken = default, bool throwOnInvalid = false)
    {
        try
        {
            await using var fs = new FileStream(
                blobFullPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var gzip = new GZipStream(fs, CompressionMode.Decompress);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[81920];
            long actualSize = 0;
            while (true)
            {
                var read = await gzip.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;

                actualSize = checked(actualSize + read);
                hasher.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            var actualHash = Convert.ToHexString(hasher.GetHashAndReset());
            if (actualSize == expectedSize
                && string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!throwOnInvalid)
                return false;

            throw new InvalidDataException(
                $"Version blob integrity check failed. Expected {expectedSize} bytes/{expectedHash}, " +
                $"got {actualSize} bytes/{actualHash}.");
        }
        catch (InvalidDataException) when (!throwOnInvalid)
        {
            return false;
        }
    }

    /// <summary>
    /// Converts a hex hash to a compressed blob path with 2-level fan-out.
    /// "ABCDEF1234..." → "AB/CD/ABCDEF1234....bin.gz"
    /// </summary>
    private static string HashToPath(string hash)
    {
        var dir1 = hash[..2];
        var dir2 = hash[2..4];
        return Path.Combine(dir1, dir2, hash + ".bin.gz");
    }

    // Stale write temporary left by an interrupted blob write:
    // ".{blobname}.{32 hex}.tmp" (WriteValidatedBlobAsync). No ".kaimo-" segment.
    private static readonly Regex VersionWriteTempPattern = new(
        @"^\..+\.[0-9a-fA-F]{32}\.tmp$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task<VersionRetentionSweepResult> SweepExpiredVersionsAsync(
        TimeSpan maxAge, int minVersionsToKeep, int maxPaths, CancellationToken cancellationToken = default)
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - maxAge;
        var keep = Math.Max(1, minVersionsToKeep);

        // Fetch one extra to detect whether more work is pending after this bounded batch.
        var paths = await _versionRepo.GetPathsWithVersionsOlderThanAsync(cutoff, maxPaths + 1, cancellationToken);
        var moreWorkPending = paths.Count > maxPaths;
        var toProcess = paths.Take(maxPaths).ToList();

        var versionsRemoved = 0;
        var removedBlobs = new List<FileVersion>();
        foreach (var (shareId, filePath) in toProcess)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The floor (keep) guarantees age-based deletion NEVER empties a file's
            // history — without it the first sweep would wipe every dormant file's history.
            var removed = await _versionRepo.DeleteOlderThanAsync(shareId, filePath, cutoff, keep);
            versionsRemoved += removed.Count;
            removedBlobs.AddRange(removed);
        }

        if (removedBlobs.Count > 0)
            await DeleteUnreferencedBlobsAsync(removedBlobs);

        if (versionsRemoved > 0)
            _logger.LogInformation(
                LogEvents.FileVersionRetentionSwept, LogMessages.FileVersionRetentionSwept,
                versionsRemoved, toProcess.Count, cutoff);

        return new VersionRetentionSweepResult(toProcess.Count, versionsRemoved, moreWorkPending);
    }

    public async Task<OrphanBlobSweepResult> ReclaimOrphanBlobsAsync(
        IReadOnlyList<string> shardPrefixes, TimeSpan minimumAge, CancellationToken cancellationToken = default)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        int examined = 0, deleted = 0, tempDeleted = 0, readCacheDeleted = 0;
        long bytesReclaimed = 0;

        var rootWithSeparator = Path.GetFullPath(_versionStorageRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        foreach (var shard in shardPrefixes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shardDir = Path.Combine(_versionStorageRoot, shard);
            if (!Directory.Exists(shardDir)) continue;

            HashSet<string> referenced;
            try
            {
                // Guard 1: the shard's whole referenced set, read in ONE query BEFORE
                // touching the filesystem (DB scope closed by the repository per call).
                referenced = await _versionRepo.GetReferencedStoragePathsUnderShardAsync(shard, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    LogEvents.FileVersionOrphanSweepFailed, ex,
                    LogMessages.FileVersionOrphanSweepFailed, shard);
                continue;
            }

            foreach (var filePath in Directory.EnumerateFiles(shardDir, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(filePath);

                if (VersionWriteTempPattern.IsMatch(fileName))
                {
                    if (TryDeleteAgedFile(filePath, nowUtc, minimumAge)) tempDeleted++;
                    continue;
                }

                if (!fileName.EndsWith(".bin.gz", StringComparison.Ordinal))
                    continue;

                examined++;
                var storagePath = Path.GetRelativePath(_versionStorageRoot, filePath);

                long size;
                DateTime lastWrite;
                try
                {
                    var info = new FileInfo(filePath);
                    size = info.Length;
                    lastWrite = info.LastWriteTimeUtc;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                // Guard 2: minimum age — the only guard that works ACROSS processes
                // (BlobLocks is in-process; writers run in Web/SmbBridge, this in Host).
                if (nowUtc - lastWrite < minimumAge) continue;
                if (referenced.Contains(storagePath)) continue;

                // Guard 3: re-check under the blob stripe lock immediately before delete.
                var blobLock = GetBlobLock(Path.GetFullPath(filePath));
                await blobLock.WaitAsync(cancellationToken);
                try
                {
                    if (await _versionRepo.IsStoragePathReferencedAsync(storagePath)) continue;
                    System.IO.File.Delete(filePath);
                    deleted++;
                    bytesReclaimed += size;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(
                        LogEvents.FileVersionBlobDeleteFailed, ex,
                        LogMessages.FileVersionBlobDeleteFailed, storagePath);
                }
                finally
                {
                    blobLock.Release();
                }
            }

            // Remove now-empty subdirectories left under the shard.
            try
            {
                foreach (var dir in Directory
                             .EnumerateDirectories(shardDir, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length))
                    RemoveEmptyParentDirectories(dir, rootWithSeparator);
                RemoveEmptyParentDirectories(shardDir, rootWithSeparator);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort tidy-up; leaving an empty directory is harmless.
            }
        }

        // Stale read-cache files: FileOptions.DeleteOnClose self-cleans on a graceful exit
        // but a hard kill leaves them behind (a fourth orphan source).
        readCacheDeleted = ReclaimReadCacheFiles(nowUtc, minimumAge, cancellationToken);

        if (deleted > 0 || tempDeleted > 0 || readCacheDeleted > 0)
            _logger.LogInformation(
                LogEvents.FileVersionOrphanBlobsReclaimed, LogMessages.FileVersionOrphanBlobsReclaimed,
                deleted, bytesReclaimed, tempDeleted, readCacheDeleted, shardPrefixes.Count);

        return new OrphanBlobSweepResult(examined, deleted, bytesReclaimed, tempDeleted, readCacheDeleted);
    }

    private bool TryDeleteAgedFile(string fullPath, DateTime nowUtc, TimeSpan minimumAge)
    {
        try
        {
            if (nowUtc - System.IO.File.GetLastWriteTimeUtc(fullPath) < minimumAge)
                return false;
            System.IO.File.Delete(fullPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private int ReclaimReadCacheFiles(DateTime nowUtc, TimeSpan minimumAge, CancellationToken cancellationToken)
    {
        var readCache = Path.Combine(_versionStorageRoot, ".read-cache");
        if (!Directory.Exists(readCache)) return 0;

        var deleted = 0;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(readCache, "*.tmp"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryDeleteAgedFile(file, nowUtc, minimumAge)) deleted++;
        }
        return deleted;
    }

    private async Task DeleteUnreferencedBlobsAsync(IEnumerable<FileVersion> removed)
    {
        foreach (var storagePath in removed.Select(v => v.StoragePath).Distinct(StringComparer.Ordinal))
            await DeleteBlobIfUnreferencedAsync(storagePath);
    }

    private async Task DeleteBlobIfUnreferencedAsync(string storagePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_versionStorageRoot, storagePath));
        var root = Path.GetFullPath(_versionStorageRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Version blob path escaped the storage root.");

        var blobLock = GetBlobLock(fullPath);
        await blobLock.WaitAsync();
        try
        {
            if (await _versionRepo.IsStoragePathReferencedAsync(storagePath)) return;
            System.IO.File.Delete(fullPath);
            RemoveEmptyParentDirectories(Path.GetDirectoryName(fullPath), root);
        }
        catch (Exception ex)
        {
            // The DB is authoritative. Do not turn an already completed user-file
            // operation into a failure when an external process temporarily locks a blob.
            _logger.LogWarning(
                LogEvents.FileVersionBlobDeleteFailed, ex,
                LogMessages.FileVersionBlobDeleteFailed, storagePath);
        }
        finally
        {
            blobLock.Release();
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
