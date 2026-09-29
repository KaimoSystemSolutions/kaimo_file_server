using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;
using System.Security.Cryptography;

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
///
/// Storage location:
///   With an <see cref="IVersionStorageLocator"/>, blobs live in the storage pool of
///   their share (outside every share folder). The fixed root passed to the
///   constructor is then only the legacy location: it is read as a fallback and
///   drained by <see cref="ReconcileStorageAsync"/>, but never written to.
///   A version is skipped (never the file operation) when its pool is low on space.
/// </summary>
public class FileVersionService : IFileVersionService
{
    private const long InMemoryReadLimitBytes = 8L * 1024 * 1024;

    // Versions are a convenience copy and must never fill the disk they share with
    // user data: keep at least this much free after writing a blob.
    private const long MinFreeBytes = 2L * 1024 * 1024 * 1024;
    private const double MinFreeRatio = 0.05;

    // Reconciliation never touches blobs this young (a concurrent writer in another
    // process may be between writing the blob and inserting its row) and removes
    // crash-left temp files older than a day.
    private static readonly TimeSpan ReconcileMinBlobAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromDays(1);
    private const long WarningIntervalMs = 5 * 60 * 1000;
    private static long _lastLowSpaceWarning = long.MinValue / 2;
    private static long _lastNoPoolWarning = long.MinValue / 2;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly SemaphoreSlim[] BlobLocks = Enumerable.Range(0, 64)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();
    private readonly IFileVersionRepository _versionRepo;
    private readonly string _versionStorageRoot;
    private readonly int _defaultMaxVersions;
    private readonly TimeSpan? _defaultMaxAge;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FileVersionService> _logger;
    private readonly IVersionStorageLocator? _locator;
    private readonly Func<string, (long Free, long Total)> _diskSpace;

    /// <param name="versionStorageRoot">
    /// The blob store without a <paramref name="storageLocator"/>; with one, the legacy
    /// location that is only read from and drained.
    /// </param>
    /// <param name="diskSpaceProbe">Free/total bytes of the volume holding a path (tests).</param>
    public FileVersionService(
        IFileVersionRepository versionRepo,
        string versionStorageRoot,
        int defaultMaxVersions = 64,
        TimeSpan? defaultMaxAge = null,
        TimeProvider? timeProvider = null,
        ILogger<FileVersionService>? logger = null,
        IVersionStorageLocator? storageLocator = null,
        Func<string, (long Free, long Total)>? diskSpaceProbe = null)
    {
        _versionRepo = versionRepo;
        _versionStorageRoot = versionStorageRoot;
        _defaultMaxVersions = defaultMaxVersions;
        _defaultMaxAge = defaultMaxAge;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<FileVersionService>.Instance;
        _locator = storageLocator;
        _diskSpace = diskSpaceProbe ?? ProbeDiskSpace;

        // The legacy location is never recreated once it has been drained.
        if (_locator is null)
            Directory.CreateDirectory(_versionStorageRoot);
    }

    public async Task<FileVersion?> CreateVersionAsync(
        Guid shareId, string filePath, Stream content, string? userId = null)
    {
        var normalizedPath = ShareRelativePath.Normalize(filePath);

        var root = await ResolveRootAsync(shareId);
        if (root is null)
        {
            if (ShouldWarn(ref _lastNoPoolWarning))
                _logger.LogWarning(
                    "Share {ShareId} is not located in a storage pool; file versions are not stored for it.",
                    shareId);
            return null;
        }
        Directory.CreateDirectory(root);

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
        var blobFullPath = Path.Combine(root, blobRelativePath);
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
                // gzip never makes a blob much larger than its content, so the content
                // size is a safe upper bound for the space this write needs.
                if (!HasRoomFor(root, contentSize))
                {
                    if (ShouldWarn(ref _lastLowSpaceWarning))
                        _logger.LogWarning(
                            "Version storage {Root} is low on free space; new file versions are skipped until space is freed.",
                            root);
                    return null;
                }

                if (blobExisted)
                    _logger.LogWarning(
                        "Replacing corrupt version blob {StoragePath}", blobRelativePath);

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
            if (createdBlob && !await IsBlobNeededInRootAsync(root, blobRelativePath))
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

        // The share's pool first; the legacy store and other pools cover blobs that
        // reconciliation has not moved yet (pre-upgrade data, shares moved between pools).
        var preferredRoot = await ResolveRootAsync(shareId);
        var blob = FindBlob(preferredRoot, version.StoragePath)
            ?? throw new FileNotFoundException(
                $"Version blob missing: {version.StoragePath}");
        var blobFullPath = blob.FullPath;

        // Keep small snapshots fast in memory, but spill large snapshots to a
        // delete-on-close seekable file so concurrent downloads cannot exhaust RAM.
        Stream output;
        if (version.Size <= InMemoryReadLimitBytes)
        {
            output = new MemoryStream((int)Math.Max(version.Size, 0));
        }
        else
        {
            var readCache = Path.Combine(preferredRoot ?? blob.Root, ".read-cache");
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

    private async Task DeleteUnreferencedBlobsAsync(IEnumerable<FileVersion> removed)
    {
        // The removed rows' share may already be gone, so every known root is checked.
        foreach (var storagePath in removed.Select(v => v.StoragePath).Distinct(StringComparer.Ordinal))
            foreach (var root in KnownRoots)
                await DeleteBlobIfUnneededAsync(root, storagePath);
    }

    /// <summary>Deletes one blob copy when no version still needs it in that root.</summary>
    private async Task<bool> DeleteBlobIfUnneededAsync(string storageRoot, string storagePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(storageRoot, storagePath));
        var root = Path.GetFullPath(storageRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Version blob path escaped the storage root.");
        if (!System.IO.File.Exists(fullPath))
            return false;

        var blobLock = GetBlobLock(fullPath);
        await blobLock.WaitAsync();
        try
        {
            if (await IsBlobNeededInRootAsync(storageRoot, storagePath)) return false;
            System.IO.File.Delete(fullPath);
            RemoveEmptyParentDirectories(Path.GetDirectoryName(fullPath), root);
            return true;
        }
        catch (Exception ex)
        {
            // The DB is authoritative. Do not turn an already completed user-file
            // operation into a failure when an external process temporarily locks a blob.
            _logger.LogWarning(ex, "Failed to delete unreferenced version blob {StoragePath}", storagePath);
            return false;
        }
        finally
        {
            blobLock.Release();
        }
    }

    /// <summary>
    /// Whether the copy of a blob in <paramref name="storageRoot"/> is still needed: a
    /// share of that pool references it, a referencing share cannot be resolved (kept
    /// conservatively), or — for the legacy store — a referencing share's pool does not
    /// have its own copy yet.
    /// </summary>
    private async Task<bool> IsBlobNeededInRootAsync(string storageRoot, string storagePath)
    {
        if (_locator is null)
            return await _versionRepo.IsStoragePathReferencedAsync(storagePath);

        bool isLegacy = SamePath(storageRoot, _versionStorageRoot);
        foreach (var shareId in await _versionRepo.GetShareIdsReferencingStoragePathAsync(storagePath))
        {
            var shareRoot = await _locator.GetRootAsync(shareId);
            if (shareRoot is null || SamePath(shareRoot, storageRoot))
                return true;
            if (isLegacy && !System.IO.File.Exists(Path.Combine(shareRoot, storagePath)))
                return true;
        }
        return false;
    }

    // ------------------ Storage location ------------------

    private async Task<string?> ResolveRootAsync(Guid shareId)
        => _locator is null ? _versionStorageRoot : await _locator.GetRootAsync(shareId);

    /// <summary>Every root that may hold blobs: the pools first, then the legacy store.</summary>
    private IEnumerable<string> KnownRoots => _locator is null
        ? [_versionStorageRoot]
        : _locator.AllRoots.Append(_versionStorageRoot);

    private (string Root, string FullPath)? FindBlob(string? preferredRoot, string storagePath)
    {
        var roots = preferredRoot is null ? KnownRoots : KnownRoots.Prepend(preferredRoot);
        foreach (var root in roots)
        {
            var fullPath = Path.Combine(root, storagePath);
            if (System.IO.File.Exists(fullPath))
                return (root, fullPath);
        }
        return null;
    }

    private bool HasRoomFor(string root, long bytes)
    {
        try
        {
            var (free, total) = _diskSpace(root);
            var reserve = Math.Max(MinFreeBytes, (long)(total * MinFreeRatio));
            return free - bytes >= reserve;
        }
        catch (Exception ex)
        {
            // Fail closed: an unknown fill level must not risk filling the volume.
            _logger.LogWarning(ex, "Could not determine the free space of version storage {Root}", root);
            return false;
        }
    }

    private static (long Free, long Total) ProbeDiskSpace(string path)
    {
        var drive = new DriveInfo(path);
        return (drive.AvailableFreeSpace, drive.TotalSize);
    }

    /// <summary>Rate limit for warnings that would otherwise fire once per written file.</summary>
    private static bool ShouldWarn(ref long lastWarningTicks)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref lastWarningTicks);
        return now - last >= WarningIntervalMs
               && Interlocked.CompareExchange(ref lastWarningTicks, now, last) == last;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            PathComparison);

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

    /// <summary>Removes write/read temp files a crashed process left behind.</summary>
    private void DeleteStaleTempFiles(string root, DateTime olderThanUtc)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).ToList())
        {
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
