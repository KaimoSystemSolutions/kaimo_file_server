using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
public partial class FileVersionService : IFileVersionService
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
    private readonly IVersioningPolicyProvider? _policyProvider;
    private readonly RecentVersionRenames _recentRenames;

    /// <param name="versionStorageRoot">
    /// The blob store without a <paramref name="storageLocator"/>; with one, the legacy
    /// location that is only read from and drained.
    /// </param>
    /// <param name="defaultMaxVersions">Retention cap used only without a <paramref name="policyProvider"/>.</param>
    /// <param name="defaultMaxAge">Retention age used only without a <paramref name="policyProvider"/>.</param>
    /// <param name="diskSpaceProbe">Free/total bytes of the volume holding a path (tests).</param>
    /// <param name="policyProvider">
    /// The global <see cref="VersioningPolicy"/>. Without one, every file is versioned and
    /// only <paramref name="defaultMaxVersions"/>/<paramref name="defaultMaxAge"/> apply.
    /// </param>
    /// <param name="recentRenames">
    /// Process-wide rename memory for safe-save sequences; a private one without it.
    /// </param>
    public FileVersionService(
        IFileVersionRepository versionRepo,
        string versionStorageRoot,
        int defaultMaxVersions = 64,
        TimeSpan? defaultMaxAge = null,
        TimeProvider? timeProvider = null,
        ILogger<FileVersionService>? logger = null,
        IVersionStorageLocator? storageLocator = null,
        Func<string, (long Free, long Total)>? diskSpaceProbe = null,
        IVersioningPolicyProvider? policyProvider = null,
        RecentVersionRenames? recentRenames = null)
    {
        _policyProvider = policyProvider;
        _recentRenames = recentRenames ?? new RecentVersionRenames();
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
        => (await CaptureVersionAsync(shareId, filePath, content, userId)).Version;

    public async Task<VersionCaptureResult> CaptureVersionAsync(
        Guid shareId, string filePath, Stream content, string? userId = null)
    {
        var normalizedPath = ShareRelativePath.Normalize(filePath);

        // 0. Global policy: checked before hashing so skipped files cost no I/O.
        var policy = _policyProvider is null ? null : await _policyProvider.GetAsync();
        if (policy is not null && SkipByPolicy(policy, normalizedPath, content) is { } skipped)
            return new VersionCaptureResult(skipped);

        var root = await ResolveRootAsync(shareId);
        if (root is null)
        {
            if (ShouldWarn(ref _lastNoPoolWarning))
                _logger.LogWarning(
                    "Share {ShareId} is not located in a storage pool; file versions are not stored for it.",
                    shareId);
            return new VersionCaptureResult(VersionCaptureOutcome.NoPool);
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
                    return new VersionCaptureResult(VersionCaptureOutcome.LowSpace);
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
                return new VersionCaptureResult(VersionCaptureOutcome.Unchanged);

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

            // Another process may have collected the blob between our validation and the
            // insert (its reference check could not see this row yet). The row now protects
            // the blob, so restoring it here closes that window for good.
            if (!System.IO.File.Exists(blobFullPath))
            {
                _logger.LogWarning(
                    "Version blob {StoragePath} vanished before its row was committed; rewriting it.",
                    blobRelativePath);
                content.Position = 0;
                await WriteValidatedBlobAsync(blobFullPath, content, hash, contentSize);
                content.Position = 0;
            }
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
        if (policy is not null)
            await ApplyPolicyRetentionAsync(shareId, normalizedPath, policy);
        else
            await ApplyRetentionAsync(shareId, normalizedPath, _defaultMaxVersions, _defaultMaxAge);

        return new VersionCaptureResult(VersionCaptureOutcome.Created, version);
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

        if (maxVersions is null && maxAge is null && _policyProvider is not null)
            return await ApplyPolicyRetentionAsync(
                shareId, normalizedPath, await _policyProvider.GetAsync());

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

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var displaced = new List<FileVersion>();

        // Safe-save (Word): the target name's history was just moved aside with the original
        // document. Bring it back first, so the new content lands on top of it. Files only:
        // a folder moved aside keeps its contents, and with them its history.
        if (_recentRenames.MovedAwayTo(shareId, newNormalized, now) is { } movedAside
            && !string.Equals(movedAside, oldNormalized, StringComparison.Ordinal)
            && (await _versionRepo.GetVersionsAsync(shareId, movedAside)).Count > 0)
        {
            displaced.AddRange(await _versionRepo.RenamePathAsync(shareId, movedAside, newNormalized));
            _logger.LogDebug(
                "Safe-save detected: history of {Path} returned from {MovedAside}.", newNormalized, movedAside);
        }
        _recentRenames.Forget(shareId, newNormalized);

        displaced.AddRange(await _versionRepo.RenamePathAsync(
            shareId, oldNormalized, newNormalized, sambaLifecycleEventId));
        _recentRenames.Record(shareId, oldNormalized, newNormalized, now);
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

}
