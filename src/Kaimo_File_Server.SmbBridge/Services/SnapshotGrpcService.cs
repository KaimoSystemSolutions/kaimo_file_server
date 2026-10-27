using System.Globalization;
using Grpc.Core;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
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
public sealed partial class SnapshotGrpcService : SnapshotService.SnapshotServiceBase
{
    // Same format Windows expects and FileVersion.ToGmtToken() emits.
    private const string GmtFormat = "'@GMT-'yyyy.MM.dd-HH.mm.ss";
    // Must match KAIMO_LOCAL_MAX_SNAPSHOT_LABELS. Samba's shadow-copy
    // enumeration is not paginated, so the contract returns the newest labels.
    internal const int MaxEnumerationLabels = 2_048;
    // One extra row tells the enumeration that labels were omitted (logged below).
    private const int DatabaseLabelLimit = MaxEnumerationLabels + 1;

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
        if (!SambaName.IsValidContext(request.Username, request.Share))
            return reply;
        CancellationToken cancellationToken =
            context?.CancellationToken ?? CancellationToken.None;

        _logger.LogInformation(
            "EnumerateSnapshots ENTER: user={User} share={Share} path=[{Path}]",
            request.Username, request.Share, request.Path);

        var user = await _auth.ResolveUserContextAsync(request.Username)
            .WaitAsync(cancellationToken);
        var share = await _shares.ResolveEnabledShareAsync(
            request.Share, cancellationToken);
        if (user is null || share is null)
        {
            _logger.LogInformation(
                "EnumerateSnapshots: unknown user or unknown/disabled share (user={User} share={Share}) -> 0",
                request.Username, request.Share);
            return reply;
        }

        if (!ShareRelativePath.TryNormalizeStrict(
                request.Path, out string normalized, allowRoot: true,
                allowInternalNamespace: false))
            return reply;

        // A concrete file -> its own version timestamps; a folder or the share
        // root ("") -> ACL-aware folder timestamps. A path with exact file
        // versions is unambiguously a file; otherwise use directory semantics.
        List<DateTime> timestamps;
        if (normalized.Length > 0)
        {
            var fileVersions = await _versions
                .GetVersionsAsync(share.Id, normalized)
                .WaitAsync(cancellationToken);
            if (fileVersions.Count > 0)
            {
                if (!await _acl.HasAccessAsync(
                        user, share.Id, normalized, false,
                        FilePermission.ListReadData)
                    .WaitAsync(cancellationToken))
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
                        .CreateForShare(share)
                        .GetFolderSnapshotTimestampsAsync(normalized, user, DatabaseLabelLimit)
                        .WaitAsync(cancellationToken);
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
                    .CreateForShare(share)
                    .GetFolderSnapshotTimestampsAsync("", user, DatabaseLabelLimit)
                    .WaitAsync(cancellationToken);
            }
            catch (UnauthorizedAccessException)
            {
                return reply;
            }
        }

        var orderedLabels = new List<string>();
        var seenLabels = new HashSet<string>(StringComparer.Ordinal);
        foreach (DateTime timestamp in timestamps.OrderByDescending(value => value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string label = timestamp.ToString(
                GmtFormat, CultureInfo.InvariantCulture);
            if (seenLabels.Add(label))
                orderedLabels.Add(label);
        }
        if (orderedLabels.Count > MaxEnumerationLabels)
        {
            _logger.LogWarning(
                "EnumerateSnapshots: user={User} share={Share} path=[{Path}] has more than {Maximum} labels; returning the newest {Maximum}",
                request.Username, request.Share, request.Path, MaxEnumerationLabels);
        }
        reply.GmtTokens.AddRange(
            orderedLabels.Take(MaxEnumerationLabels));

        _logger.LogInformation(
            "EnumerateSnapshots: user={User} share={Share} path=[{Path}] -> {Count} tokens",
            request.Username, request.Share, request.Path, reply.GmtTokens.Count);
        return reply;
    }

    public override async Task<ResolveVersionReply> ResolveVersion(
        ResolveVersionRequest request, ServerCallContext context)
    {
        var notFound = new ResolveVersionReply { Found = false };
        if (!SambaName.IsValidContext(request.Username, request.Share))
            return notFound;

        CancellationToken requestCancellation =
            context?.CancellationToken ?? CancellationToken.None;

        // Diagnostic: shows whether the VFS module reaches the bridge for a file open,
        // and with what path/token.
        _logger.LogDebug(
            "ResolveVersion ENTER: user={User} share={Share} path=[{Path}] token={Token}",
            request.Username, request.Share, request.Path, request.GmtToken);

        var user = await _auth.ResolveUserContextAsync(request.Username)
            .WaitAsync(requestCancellation);
        var share = await _shares.ResolveEnabledShareAsync(
            request.Share, requestCancellation);
        if (user is null || share is null)
        {
            _logger.LogWarning(
                "ResolveVersion: unknown user or unknown/disabled share (user={User} share={Share}) -> notFound",
                request.Username, request.Share);
            return notFound;
        }

        if (!ShareRelativePath.TryNormalizeStrict(
                request.Path, out string normalized, allowRoot: true,
                allowInternalNamespace: false))
            return notFound;

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
            return await ResolveHistoricalDirectoryAsync(
                request, user, share, normalized, ts.Value, requestCancellation);

        return await ResolveVersionFileAsync(
            request, user, share, normalized, version, requestCancellation);
    }

    /// <summary>A concrete versioned file: checks read access and materializes its content.</summary>
    private async Task<ResolveVersionReply> ResolveVersionFileAsync(
        ResolveVersionRequest request, UserContext user, ShareDefinition share,
        string normalized, FileVersion version, CancellationToken requestCancellation)
    {
        var notFound = new ResolveVersionReply { Found = false };

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
            if (!ShareRelativePath.TryNormalizeStrict(
                    version.FilePath, out string normalizedVersionPath,
                    allowRoot: false, allowInternalNamespace: false))
                throw new InvalidDataException(
                    "Version contains an invalid file path.");
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

}