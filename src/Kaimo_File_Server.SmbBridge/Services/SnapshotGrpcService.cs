using System.Globalization;
using System.Security.Cryptography;
using Grpc.Core;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.SmbBridge.Grpc;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// gRPC facade for SMB "Previous Versions" / @GMT snapshots (Phase 5). Windows
/// enumerates a file's versions via FSCTL_SRV_ENUMERATE_SNAPSHOTS and then opens
/// a path prefixed with an <c>@GMT-</c> token to read that version. Kaimo stores
/// versions as gzip-compressed, content-addressed blobs (not filesystem snapshot
/// directories), so the native <c>vfs_shadow_copy2</c> cannot serve them. The
/// custom VFS module calls this service to (1) enumerate the tokens and (2)
/// materialize a chosen version as a plain, decompressed file in an isolated
/// cache outside every client-visible share. The native module serves that file
/// directly from the configured internal cache root.
///
/// Version lookup remains backed by <see cref="IFileVersionService"/>. Folder
/// projections run through <see cref="IFileService"/> so its directory and
/// per-child ACL checks are reused without duplicating policy.
/// </summary>
public sealed class SnapshotGrpcService : SnapshotService.SnapshotServiceBase
{
    // Same format Windows expects and FileVersion.ToGmtToken() emits.
    private const string GmtFormat = "'@GMT-'yyyy.MM.dd-HH.mm.ss";

    private readonly IShareRepository _shares;
    private readonly IAuthenticationLookup _auth;
    private readonly IAclService _acl;
    private readonly IFileVersionService _versions;
    private readonly IFileServiceFactory _fileServices;
    private readonly SnapshotCacheLeaseManager _leases;
    private readonly SnapshotMaterializationLimiter _materializationLimiter;
    private readonly ILogger<SnapshotGrpcService> _logger;
    private readonly string _cacheRoot;

    public SnapshotGrpcService(
        IShareRepository shares,
        IAuthenticationLookup auth,
        IAclService acl,
        IFileVersionService versions,
        IFileServiceFactory fileServices,
        SnapshotCacheLeaseManager leases,
        SnapshotMaterializationLimiter materializationLimiter,
        IConfiguration configuration,
        ILogger<SnapshotGrpcService> logger)
    {
        _shares = shares;
        _auth = auth;
        _acl = acl;
        _versions = versions;
        _fileServices = fileServices;
        _leases = leases;
        _materializationLimiter = materializationLimiter;
        _cacheRoot = SnapshotCache.ConfiguredRoot(configuration);
        _logger = logger;
    }

    public override async Task<EnumerateSnapshotsReply> EnumerateSnapshots(
        EnumerateSnapshotsRequest request, ServerCallContext context)
    {
        var reply = new EnumerateSnapshotsReply();

        _logger.LogInformation(
            "EnumerateSnapshots ENTER: user={User} share={Share} path=[{Path}]",
            request.Username, request.Share, request.Path);

        var user = await _auth.ResolveUserContextAsync(request.Username);
        var share = await _shares.GetByNameAsync(request.Share);
        if (user is null || share is null)
        {
            _logger.LogInformation(
                "EnumerateSnapshots: unknown user/share (user={User} share={Share}) -> 0",
                request.Username, request.Share);
            return reply;
        }

        if (!ShareRelativePath.IsValid(request.Path))
            return reply;

        string normalized = ShareRelativePath.Normalize(request.Path);
        if (normalized == ".") normalized = ""; // SMB share-root atname -> root

        // A concrete file -> its own version timestamps; a folder or the share
        // root ("") -> ACL-aware folder timestamps. A path with exact file
        // versions is unambiguously a file; otherwise use directory semantics.
        List<DateTime> timestamps;
        if (normalized.Length > 0)
        {
            var fileVersions = await _versions.GetVersionsAsync(share.Id, normalized);
            if (fileVersions.Count > 0)
            {
                if (!await _acl.HasAccessAsync(
                        user, share.Id, normalized, false,
                        FilePermission.ListReadData))
                    return reply;
                timestamps = fileVersions
                    .Select(v => v.SnapshotTimestampUtc)
                    .ToList();
            }
            else
            {
                try
                {
                    timestamps = await _fileServices
                        .CreateForShare(share.Id, share.Path)
                        .GetFolderSnapshotTimestampsAsync(normalized, user);
                }
                catch (UnauthorizedAccessException)
                {
                    return reply;
                }
            }
        }
        else
        {
            try
            {
                timestamps = await _fileServices
                    .CreateForShare(share.Id, share.Path)
                    .GetFolderSnapshotTimestampsAsync("", user);
            }
            catch (UnauthorizedAccessException)
            {
                return reply;
            }
        }

        foreach (var ts in timestamps.Distinct().OrderByDescending(t => t))
            reply.GmtTokens.Add(ts.ToString(GmtFormat, CultureInfo.InvariantCulture));

        _logger.LogInformation(
            "EnumerateSnapshots: user={User} share={Share} path=[{Path}] -> {Count} tokens",
            request.Username, request.Share, request.Path, reply.GmtTokens.Count);
        return reply;
    }

    public override async Task<ResolveVersionReply> ResolveVersion(
        ResolveVersionRequest request, ServerCallContext context)
    {
        var notFound = new ResolveVersionReply { Found = false };
        CancellationToken requestCancellation =
            context?.CancellationToken ?? CancellationToken.None;

        // Diagnostic: prove whether the VFS module reaches the bridge for a file open,
        // and with what path/token. (Temporary high-visibility trace for @GMT debugging.)
        _logger.LogInformation(
            "ResolveVersion ENTER: user={User} share={Share} path=[{Path}] token={Token}",
            request.Username, request.Share, request.Path, request.GmtToken);

        var user = await _auth.ResolveUserContextAsync(request.Username)
            .WaitAsync(requestCancellation);
        var share = await _shares.GetByNameAsync(request.Share)
            .WaitAsync(requestCancellation);
        if (user is null || share is null)
        {
            _logger.LogWarning(
                "ResolveVersion: unknown user/share (user={User} share={Share}) -> notFound",
                request.Username, request.Share);
            return notFound;
        }

        if (!ShareRelativePath.IsValid(request.Path))
            return notFound;

        string normalized = ShareRelativePath.Normalize(request.Path);
        if (normalized == ".") normalized = ""; // SMB share-root atname -> root

        var ts = FileVersion.ParseGmtToken(request.GmtToken);
        if (ts is null)
        {
            _logger.LogWarning("ResolveVersion: bad @GMT token '{Token}'", request.GmtToken);
            return notFound;
        }

        try
        {
            SnapshotCache.EnsureIsolatedFromShare(_cacheRoot, share.Path);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "ResolveVersion: snapshot cache overlaps share {Share}; refusing materialization.",
                request.Share);
            return notFound;
        }

        var version = await _versions
            .GetVersionAtAsync(share.Id, normalized, ts.Value)
            .WaitAsync(requestCancellation);

        // Point-in-time resolution (the actual @GMT "restore/open" fix). Folder
        // "Previous Versions" enumerates ONE share-wide timestamp per snapshot (the
        // @GMT token), which is almost never the exact per-file version time. An
        // exact-match lookup (GetVersionAtAsync) therefore misses the individual
        // file, so opening it over SMB failed with OBJECT_NAME_NOT_FOUND (Notepad),
        // fell back to the live file (Notepad++ showed current content), and made
        // Windows hide the file-level version list entirely. Resolve a concrete file
        // with the SAME "newest version at or before the token" semantics the folder
        // listing uses, so the token consistently maps to the right version.
        if (version is null && normalized.Length > 0)
        {
            var fileVersions = await _versions.GetVersionsAsync(share.Id, normalized)
                .WaitAsync(requestCancellation);
            version = fileVersions
                .Where(v => v.SnapshotTimestampUtc <= ts.Value)
                .OrderByDescending(v => v.SnapshotTimestampUtc)
                .FirstOrDefault();
            _logger.LogInformation(
                "ResolveVersion: point-in-time lookup path=[{Path}] ts<={Ts:o} -> {Result} (of {Total} versions)",
                normalized, ts.Value,
                version is null ? "no match" : $"v#{version.VersionNumber}@{version.SnapshotTimestampUtc:o}",
                fileVersions.Count);
        }

        // Directory component of a snapshot path. SMB resolves EVERY parent directory
        // with the timewarp too (smbd stats "weqr" before opening "weqr/file"). Those
        // have no file version, so without special handling the whole open fails at the
        // parent with OBJECT_NAME_NOT_FOUND. We hand back a real (empty) cache directory
        // mirroring the historical tree so traversal reaches the versioned file below.
        // A path is a historical directory if any versioned file existed under it at or
        // before the snapshot time (or it's the share root).
        if (version is null)
        {
            List<FileVersion> under;
            using CancellationTokenSource folderBudget =
                _materializationLimiter.CreateRequestBudget(
                    requestCancellation);
            CancellationToken folderCancellation = folderBudget.Token;
            try
            {
                // P0-05: use the central ACL-aware path. It checks the requested
                // directory with isDirectory=true and batch-filters every child.
                under = await _fileServices
                    .CreateForShare(share.Id, share.Path)
                    .GetFolderSnapshotAsync(
                        normalized, ts.Value, user, folderCancellation);
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogInformation(
                    "ResolveVersion DENY directory: user={User} share={Share} path=[{Path}]",
                    request.Username, request.Share, request.Path);
                return notFound;
            }
            catch (OperationCanceledException)
                when (requestCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(ex,
                    "ResolveVersion: folder snapshot lookup cancelled or timed out share={Share} path=[{Path}] token={Token}",
                    request.Share, request.Path, request.GmtToken);
                return notFound;
            }

            if (normalized.Length == 0 || under.Count > 0)
            {
                string cacheScope = user.User.Id.ToString("N");
                string shareScope = SnapshotCache.RelativeShareRootFor(share.Id);
                string dirRel = normalized.Length == 0
                    ? $"{shareScope}/{request.GmtToken}/{cacheScope}"
                    : $"{shareScope}/{request.GmtToken}/{cacheScope}/{normalized}";
                string dirFull = GetCachePath(_cacheRoot, dirRel);
                string scopeFull = GetCachePath(
                    _cacheRoot,
                    $"{shareScope}/{request.GmtToken}/{cacheScope}");
                string folderLeaseId;
                try
                {
                    using SnapshotMaterializationLimiter.Reservation reservation =
                        await _materializationLimiter.ReserveAsync(
                            under, folderCancellation);
                    CancellationToken materializationCancellation =
                        reservation.CancellationToken;

                    string? existingLeaseId =
                        await _leases.TryAcquireExistingHandoffAsync(
                            _cacheRoot, share.Id, request.GmtToken,
                            async cancellationToken =>
                                await HasExpectedProjectionAsync(
                                    scopeFull, dirFull, under,
                                    cancellationToken),
                            materializationCancellation);
                    if (existingLeaseId is not null)
                    {
                        _logger.LogInformation(
                            "ResolveVersion: reused verified directory projection path=[{Path}] token={Token}",
                            request.Path, request.GmtToken);
                        return new ResolveVersionReply
                        {
                            Found = true,
                            CachePath = dirRel,
                            Size = 0,
                            LeaseId = existingLeaseId
                        };
                    }

                    using SnapshotCacheLeaseManager.MaterializationLease
                        materializationLease =
                        await _leases.AcquireMaterializationAsync(
                            _cacheRoot, share.Id, request.GmtToken,
                            materializationCancellation);
                    materializationCancellation.ThrowIfCancellationRequested();
                    SnapshotCache.EnsureDirectory(dirFull);
                    // A user's projection may contain files materialized before an
                    // ACL revocation. Remove everything below this directory that
                    // is not in the freshly filtered snapshot before returning it.
                    ReconcileUserProjection(
                        _cacheRoot, share.Id, request.GmtToken, cacheScope,
                        normalized, under, materializationCancellation);
                    // Stamp the cache-age marker so the evictor keys off the real
                    // materialization time, not the historical file mtimes.
                    SnapshotCache.TouchMarker(
                        _cacheRoot, share.Id, request.GmtToken);
                    // Eager full-folder materialization. Files opened RELATIVE to a
                    // resolved snapshot directory bypass the timewarp logic (their parent
                    // fsp already points at the cache dir, twrp cleared), so smbd reads
                    // them straight from disk — an empty cache dir would list nothing and
                    // opening would fail with "path does not exist". GetFolderSnapshotAsync
                    // gave us each file's state as of the snapshot; write them all now with
                    // their historical mtimes so browsing AND opening work natively.
                    // Per-file resilience: one unreadable version must not sink the folder.
                    var createdFiles = new List<string>();
                    try
                    {
                        foreach (var fv in under)
                        {
                            materializationCancellation.ThrowIfCancellationRequested();
                            string destination = ProjectionPath(
                                share.Id, request.GmtToken, cacheScope, fv);
                            bool existed = File.Exists(destination);
                            try
                            {
                                await MaterializeVersionAsync(
                                    share.Id, request.GmtToken,
                                    cacheScope, fv,
                                    materializationCancellation);
                                if (!existed)
                                    createdFiles.Add(destination);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex,
                                    "ResolveVersion: skipping file [{File}] in dir snapshot", fv.FilePath);
                            }
                        }
                        materializationCancellation.ThrowIfCancellationRequested();
                    }
                    catch
                    {
                        CleanupAbandonedProjection(createdFiles);
                        throw;
                    }
                    try { Directory.SetLastWriteTimeUtc(dirFull, ts.Value); }
                    catch (Exception ex) { _logger.LogWarning(ex, "SetLastWriteTimeUtc (dir) failed for {Dir} (non-fatal)", dirFull); }
                    folderLeaseId = materializationLease.PublishHandoff();
                }
                catch (SnapshotMaterializationLimitException ex)
                {
                    _logger.LogWarning(ex,
                        "ResolveVersion: folder snapshot exceeds materialization quota share={Share} path=[{Path}] token={Token}",
                        request.Share, request.Path, request.GmtToken);
                    return notFound;
                }
                catch (OperationCanceledException)
                    when (requestCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogWarning(ex,
                        "ResolveVersion: folder snapshot cancelled or timed out share={Share} path=[{Path}] token={Token}",
                        request.Share, request.Path, request.GmtToken);
                    return notFound;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "ResolveVersion: dir materialize failed share={Share} path=[{Path}] token={Token}",
                        request.Share, request.Path, request.GmtToken);
                    return notFound;
                }

                _logger.LogInformation(
                    "ResolveVersion: user={User} share={Share} path=[{Path}] token={Token} -> DIR {Cache} ({Count} files)",
                    request.Username, request.Share, request.Path, request.GmtToken, dirRel, under.Count);
                return new ResolveVersionReply
                {
                    Found = true,
                    CachePath = dirRel,
                    Size = 0,
                    LeaseId = folderLeaseId
                };
            }
            _logger.LogWarning(
                "ResolveVersion: NO version and NOT a historical dir -> notFound. path=[{Path}] token={Token}",
                request.Path, request.GmtToken);
            return notFound;
        }

        // Reading a concrete version is a file read, never a directory check.
        if (!await _acl.HasAccessAsync(
                user, share.Id, normalized, false,
                FilePermission.ListReadData).WaitAsync(requestCancellation))
        {
            _logger.LogInformation(
                "ResolveVersion DENY file: user={User} share={Share} path=[{Path}]",
                request.Username, request.Share, request.Path);
            return notFound;
        }

        // A concrete versioned file: materialize its content (decompressed) with the
        // historical mtime and hand back the cache-root-relative path.
        string cacheRel;
        string leaseId;
        try
        {
            ValidateVersionContentMetadata(version);
            string cacheScope = user.User.Id.ToString("N");
            string normalizedVersionPath =
                ShareRelativePath.Normalize(version.FilePath);
            string shareScope =
                SnapshotCache.RelativeShareRootFor(share.Id);
            cacheRel =
                $"{shareScope}/{request.GmtToken}/{cacheScope}/{normalizedVersionPath}";
            string existingCacheFull =
                GetCachePath(_cacheRoot, cacheRel);
            string? existingLeaseId =
                await _leases.TryAcquireExistingHandoffAsync(
                    _cacheRoot, share.Id, request.GmtToken,
                    async cancellationToken =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return await HasExpectedContentAsync(
                            existingCacheFull, version.Size,
                            version.ContentHash, cancellationToken);
                    },
                    requestCancellation);
            if (existingLeaseId is not null)
            {
                _logger.LogInformation(
                    "ResolveVersion: reused verified file projection path=[{Path}] token={Token}",
                    request.Path, request.GmtToken);
                return new ResolveVersionReply
                {
                    Found = true,
                    CachePath = cacheRel,
                    Size = version.Size,
                    LeaseId = existingLeaseId
                };
            }

            using SnapshotCacheLeaseManager.MaterializationLease
                materializationLease =
                await _leases.AcquireMaterializationAsync(
                    _cacheRoot, share.Id, request.GmtToken,
                    requestCancellation);
            cacheRel = await MaterializeVersionAsync(
                share.Id, request.GmtToken, cacheScope, version,
                requestCancellation);
            // Stamp the cache-age marker (real materialization time) for the evictor.
            SnapshotCache.TouchMarker(
                _cacheRoot, share.Id, request.GmtToken);
            leaseId = materializationLease.PublishHandoff();
        }
        catch (OperationCanceledException)
            when (requestCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ResolveVersion: materialize failed for share={Share} path=[{Path}] token={Token}",
                request.Share, request.Path, request.GmtToken);
            return notFound;
        }

        string cacheFull = GetCachePath(_cacheRoot, cacheRel);
        bool cacheExists = System.IO.File.Exists(cacheFull);
        _logger.LogInformation(
            "ResolveVersion: user={User} share={Share} path=[{Path}] token={Token} -> FILE {Cache} ({Size} B, onDisk={Exists})",
            request.Username, request.Share, request.Path, request.GmtToken, cacheRel, version.Size, cacheExists);

        return new ResolveVersionReply
        {
            Found = true,
            CachePath = cacheRel,
            Size = version.Size,
            LeaseId = leaseId,
        };
    }

    public override Task<ReleaseVersionLeaseReply> ReleaseVersionLease(
        ReleaseVersionLeaseRequest request, ServerCallContext context)
    {
        bool released = _leases.ReleaseHandoff(request.LeaseId);
        if (!released)
            _logger.LogDebug(
                "Snapshot lease {LeaseId} was already released, expired, or unknown.",
                request.LeaseId);
        return Task.FromResult(new ReleaseVersionLeaseReply
        {
            Released = released
        });
    }

    /// <summary>
    /// Writes one version's decompressed content into the isolated snapshot cache
    /// (<c>&lt;cache-root&gt;/&lt;share-id&gt;/&lt;@GMT&gt;/&lt;user-id&gt;/&lt;filePath&gt;</c>) and stamps
    /// the historical modification time onto it. Idempotent: a version is
    /// content-addressed and immutable, so an existing cache file with the exact
    /// expected size and SHA-256 hash is reused; the mtime is (re)applied every
    /// call. New content is verified in a same-directory temporary file and
    /// atomically published, so readers never observe a partial final file.
    ///
    /// The historical mtime matters twice: Windows "Previous Versions" HIDES any
    /// snapshot whose file mtime equals the live file's, and distinct per-version
    /// mtimes let Explorer tell the versions apart. Returns a cache-root-relative
    /// path (forward slashes); the VFS validates it and joins its configured root.
    /// </summary>
    private async Task<string> MaterializeVersionAsync(
        Guid shareId, string gmtToken,
        string cacheScope, FileVersion v,
        CancellationToken cancellationToken)
    {
        if (!ShareRelativePath.IsValid(v.FilePath) ||
            ShareRelativePath.Normalize(v.FilePath).Length == 0)
            throw new InvalidDataException("Version contains an invalid file path.");

        string normalizedPath = ShareRelativePath.Normalize(v.FilePath);
        string shareScope = SnapshotCache.RelativeShareRootFor(shareId);
        string rel = $"{shareScope}/{gmtToken}/{cacheScope}/{normalizedPath}";
        string scopeRoot = SnapshotCache.EnsureUserScope(
            _cacheRoot, shareId, gmtToken, cacheScope);
        string full = GetScopedCachePath(scopeRoot, normalizedPath);

        ValidateVersionContentMetadata(v);
        bool reusable = await HasExpectedContentAsync(
            full, v.Size, v.ContentHash, cancellationToken);
        if (!reusable)
        {
            SnapshotCache.EnsureDirectory(Path.GetDirectoryName(full)!);
            string temporary = full + ".kaimo-tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using var content = await _versions.ReadVersionAsync(
                    shareId, v.FilePath, v.SnapshotTimestampUtc,
                    cancellationToken);
                await WriteVerifiedTemporaryFileAsync(
                    content, temporary, v.Size, v.ContentHash,
                    cancellationToken);

                SnapshotCache.SetReadOnlyProjectionMode(temporary);
                File.SetLastWriteTimeUtc(temporary, v.SnapshotTimestampUtc);

                // The temporary file lives beside the destination, so rename is
                // atomic and cannot cross filesystem boundaries. Concurrent
                // publishers may replace one another, but only after both have
                // independently verified the same immutable version content.
                File.Move(temporary, full, overwrite: true);
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to remove abandoned snapshot temporary file {Path}",
                        temporary);
                }
            }
        }
        SnapshotCache.SetReadOnlyProjectionMode(full);

        // Best-effort: stamp the historical mtime (what Windows "Previous Versions"
        // uses to tell versions apart / hide the one identical to the live file).
        // Creation time (birthtime) is deliberately NOT set — some bind-mount
        // filesystems (drvfs/9p on Docker Desktop) cannot set it and throw, which
        // must never break materialization. mtime failures are likewise non-fatal.
        try
        {
            File.SetLastWriteTimeUtc(full, v.SnapshotTimestampUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SetLastWriteTimeUtc failed for {Path} (non-fatal)", full);
        }
        return rel;
    }

    private static void ValidateVersionContentMetadata(FileVersion version)
    {
        if (version.Size < 0)
            throw new InvalidDataException("Version contains a negative size.");
        if (string.IsNullOrEmpty(version.ContentHash) ||
            version.ContentHash.Length != 64 ||
            !version.ContentHash.All(Uri.IsHexDigit))
            throw new InvalidDataException(
                "Version contains an invalid SHA-256 content hash.");
    }

    private static async Task<bool> HasExpectedContentAsync(
        string path, long expectedLength, string expectedHash,
        CancellationToken cancellationToken)
    {
        var existing = new FileInfo(path);
        if (!existing.Exists || existing.Length != expectedLength)
            return false;

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        string actualHash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        return string.Equals(
            actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static async ValueTask<bool> HasExpectedProjectionAsync(
        string scopeRoot,
        string directory,
        IReadOnlyCollection<FileVersion> expectedVersions,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
            return false;

        var expectedFiles = new Dictionary<string, FileVersion>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        foreach (FileVersion version in expectedVersions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ShareRelativePath.IsValid(version.FilePath) ||
                ShareRelativePath.Normalize(version.FilePath).Length == 0)
                return false;
            ValidateVersionContentMetadata(version);
            string fullPath = GetScopedCachePath(
                scopeRoot, version.FilePath);
            expectedFiles[fullPath] = version;
        }

        string[] actualFiles;
        try
        {
            actualFiles = Directory.GetFiles(
                directory, "*", SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        if (actualFiles.Length != expectedFiles.Count)
            return false;

        foreach ((string path, FileVersion version) in expectedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await HasExpectedContentAsync(
                    path, version.Size, version.ContentHash,
                    cancellationToken))
                return false;
        }
        return true;
    }

    private static async Task WriteVerifiedTemporaryFileAsync(
        Stream content,
        string temporaryPath,
        long expectedLength,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long written = 0;

        while (true)
        {
            int read = await content.ReadAsync(
                buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;
            if (written > expectedLength - read)
                throw new InvalidDataException(
                    "Version content exceeds its declared size.");

            await output.WriteAsync(
                buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
            written += read;
        }

        if (written != expectedLength)
            throw new InvalidDataException(
                $"Version content length {written} does not match declared size {expectedLength}.");

        string actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(
                actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Version content does not match its declared SHA-256 hash.");

        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private static void ReconcileUserProjection(
        string cacheRoot,
        Guid shareId,
        string gmtToken,
        string cacheScope,
        string folderPath,
        IReadOnlyCollection<FileVersion> readableVersions,
        CancellationToken cancellationToken)
    {
        string scopeRoot = SnapshotCache.EnsureUserScope(
            cacheRoot, shareId, gmtToken, cacheScope);
        string normalizedFolder = ShareRelativePath.Normalize(folderPath);
        string folderFull = normalizedFolder.Length == 0
            ? scopeRoot
            : Path.Combine(
                scopeRoot,
                normalizedFolder.Replace('/', Path.DirectorySeparatorChar));
        SnapshotCache.EnsureDirectory(folderFull);

        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var allowed = readableVersions
            .Where(v => ShareRelativePath.IsValid(v.FilePath))
            .Select(v => GetScopedCachePath(scopeRoot, v.FilePath))
            .ToHashSet(pathComparer);

        foreach (string file in Directory.EnumerateFiles(
                     folderFull, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!allowed.Contains(Path.GetFullPath(file)))
                File.Delete(file);
        }

        // Remove stale empty directories left behind by revoked files, but retain
        // the requested directory that Samba is about to traverse.
        foreach (string directory in Directory.EnumerateDirectories(
                     folderFull, "*", SearchOption.AllDirectories)
                 .OrderByDescending(path => path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }

    private string ProjectionPath(
        Guid shareId, string gmtToken, string cacheScope, FileVersion version)
    {
        string scopeRoot = SnapshotCache.EnsureUserScope(
            _cacheRoot, shareId, gmtToken, cacheScope);
        return GetScopedCachePath(scopeRoot, version.FilePath);
    }

    private void CleanupAbandonedProjection(IEnumerable<string> createdFiles)
    {
        foreach (string path in createdFiles.Reverse())
        {
            try
            {
                File.Delete(path);
                string? directory = Path.GetDirectoryName(path);
                while (directory is not null &&
                       !string.Equals(
                           Path.GetFullPath(directory),
                           Path.GetFullPath(_cacheRoot),
                           OperatingSystem.IsWindows()
                               ? StringComparison.OrdinalIgnoreCase
                               : StringComparison.Ordinal) &&
                       Directory.Exists(directory) &&
                       !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                    directory = Path.GetDirectoryName(directory);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to remove abandoned snapshot projection file {Path}",
                    path);
            }
        }
    }

    private static string GetScopedCachePath(
        string scopeRoot, string relativePath)
    {
        string root = Path.GetFullPath(scopeRoot);
        string candidate = Path.GetFullPath(Path.Combine(
            root,
            ShareRelativePath.Normalize(relativePath)
                .Replace('/', Path.DirectorySeparatorChar)));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(prefix, comparison))
            throw new InvalidDataException(
                "Version path escapes the user snapshot cache scope.");
        return candidate;
    }

    private static string GetCachePath(string cacheRoot, string relativePath)
    {
        if (!ShareRelativePath.IsValid(relativePath) ||
            ShareRelativePath.Normalize(relativePath).Length == 0)
            throw new InvalidDataException("Invalid snapshot cache-relative path.");
        return GetScopedCachePath(cacheRoot, relativePath);
    }
}
