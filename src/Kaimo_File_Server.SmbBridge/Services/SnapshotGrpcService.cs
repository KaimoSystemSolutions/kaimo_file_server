using System.Globalization;
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
/// materialize a chosen version as a plain, decompressed file inside the share,
/// which the module then serves natively — the data path stays native, exactly
/// like live files.
///
/// Version lookup remains backed by <see cref="IFileVersionService"/>. Folder
/// projections run through <see cref="IFileService"/> so its directory and
/// per-child ACL checks are reused without duplicating policy.
/// </summary>
public sealed class SnapshotGrpcService : SnapshotService.SnapshotServiceBase
{
    // Same format Windows expects and FileVersion.ToGmtToken() emits.
    private const string GmtFormat = "'@GMT-'yyyy.MM.dd-HH.mm.ss";

    // Hidden per-share directory that holds materialized (decompressed) versions.
    // Lives INSIDE the share directory so Samba's path validation accepts the
    // redirect (no wide-link/outside-share issues). Hidden from listings by the
    // VFS readdir filter (names starting with ".kaimo-"). Shared with the evictor
    // (SnapshotCacheCleanupService) via SnapshotCache.
    private const string CacheDirName = SnapshotCache.DirName;

    private readonly IShareRepository _shares;
    private readonly IAuthenticationLookup _auth;
    private readonly IAclService _acl;
    private readonly IFileVersionService _versions;
    private readonly IFileServiceFactory _fileServices;
    private readonly ILogger<SnapshotGrpcService> _logger;

    public SnapshotGrpcService(
        IShareRepository shares,
        IAuthenticationLookup auth,
        IAclService acl,
        IFileVersionService versions,
        IFileServiceFactory fileServices,
        ILogger<SnapshotGrpcService> logger)
    {
        _shares = shares;
        _auth = auth;
        _acl = acl;
        _versions = versions;
        _fileServices = fileServices;
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

        // Diagnostic: prove whether the VFS module reaches the bridge for a file open,
        // and with what path/token. (Temporary high-visibility trace for @GMT debugging.)
        _logger.LogInformation(
            "ResolveVersion ENTER: user={User} share={Share} path=[{Path}] token={Token}",
            request.Username, request.Share, request.Path, request.GmtToken);

        var user = await _auth.ResolveUserContextAsync(request.Username);
        var share = await _shares.GetByNameAsync(request.Share);
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

        var version = await _versions.GetVersionAtAsync(share.Id, normalized, ts.Value);

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
            var fileVersions = await _versions.GetVersionsAsync(share.Id, normalized);
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
            try
            {
                // P0-05: use the central ACL-aware path. It checks the requested
                // directory with isDirectory=true and batch-filters every child.
                under = await _fileServices
                    .CreateForShare(share.Id, share.Path)
                    .GetFolderSnapshotAsync(normalized, ts.Value, user);
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogInformation(
                    "ResolveVersion DENY directory: user={User} share={Share} path=[{Path}]",
                    request.Username, request.Share, request.Path);
                return notFound;
            }

            if (normalized.Length == 0 || under.Count > 0)
            {
                string cacheScope = user.User.Id.ToString("N");
                string dirRel = normalized.Length == 0
                    ? $"{CacheDirName}/{request.GmtToken}/{cacheScope}"
                    : $"{CacheDirName}/{request.GmtToken}/{cacheScope}/{normalized}";
                string dirFull = Path.Combine(share.Path, dirRel.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    Directory.CreateDirectory(dirFull);
                    // A user's projection may contain files materialized before an
                    // ACL revocation. Remove everything below this directory that
                    // is not in the freshly filtered snapshot before returning it.
                    ReconcileUserProjection(
                        share.Path, request.GmtToken, cacheScope,
                        normalized, under);
                    // Stamp the cache-age marker so the evictor keys off the real
                    // materialization time, not the historical file mtimes.
                    SnapshotCache.TouchMarker(share.Path, request.GmtToken);
                    // Eager full-folder materialization. Files opened RELATIVE to a
                    // resolved snapshot directory bypass the timewarp logic (their parent
                    // fsp already points at the cache dir, twrp cleared), so smbd reads
                    // them straight from disk — an empty cache dir would list nothing and
                    // opening would fail with "path does not exist". GetFolderSnapshotAsync
                    // gave us each file's state as of the snapshot; write them all now with
                    // their historical mtimes so browsing AND opening work natively.
                    // Per-file resilience: one unreadable version must not sink the folder.
                    foreach (var fv in under)
                    {
                        try
                        {
                            await MaterializeVersionAsync(
                                share.Id, share.Path, request.GmtToken,
                                cacheScope, fv);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "ResolveVersion: skipping file [{File}] in dir snapshot", fv.FilePath);
                        }
                    }
                    try { Directory.SetLastWriteTimeUtc(dirFull, ts.Value); }
                    catch (Exception ex) { _logger.LogWarning(ex, "SetLastWriteTimeUtc (dir) failed for {Dir} (non-fatal)", dirFull); }
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
                return new ResolveVersionReply { Found = true, CachePath = dirRel, Size = 0 };
            }
            _logger.LogWarning(
                "ResolveVersion: NO version and NOT a historical dir -> notFound. path=[{Path}] token={Token}",
                request.Path, request.GmtToken);
            return notFound;
        }

        // Reading a concrete version is a file read, never a directory check.
        if (!await _acl.HasAccessAsync(
                user, share.Id, normalized, false,
                FilePermission.ListReadData))
        {
            _logger.LogInformation(
                "ResolveVersion DENY file: user={User} share={Share} path=[{Path}]",
                request.Username, request.Share, request.Path);
            return notFound;
        }

        // A concrete versioned file: materialize its content (decompressed) with the
        // historical mtime and hand back the in-share cache path.
        string cacheRel;
        try
        {
            string cacheScope = user.User.Id.ToString("N");
            cacheRel = await MaterializeVersionAsync(
                share.Id, share.Path, request.GmtToken, cacheScope, version);
            // Stamp the cache-age marker (real materialization time) for the evictor.
            SnapshotCache.TouchMarker(share.Path, request.GmtToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ResolveVersion: materialize failed for share={Share} path=[{Path}] token={Token}",
                request.Share, request.Path, request.GmtToken);
            return notFound;
        }

        string cacheFull = Path.Combine(share.Path, cacheRel.Replace('/', Path.DirectorySeparatorChar));
        bool cacheExists = System.IO.File.Exists(cacheFull);
        _logger.LogInformation(
            "ResolveVersion: user={User} share={Share} path=[{Path}] token={Token} -> FILE {Cache} ({Size} B, onDisk={Exists})",
            request.Username, request.Share, request.Path, request.GmtToken, cacheRel, version.Size, cacheExists);

        return new ResolveVersionReply
        {
            Found = true,
            CachePath = cacheRel,
            Size = version.Size,
        };
    }

    /// <summary>
    /// Writes one version's decompressed content into the in-share snapshot cache
    /// (<c>&lt;share&gt;/.kaimo-snapshots/&lt;@GMT&gt;/&lt;user-id&gt;/&lt;filePath&gt;</c>) and stamps
    /// the historical modification time onto it. Idempotent: a version is
    /// content-addressed and immutable, so an existing cache file of the right
    /// (uncompressed) size is reused; the mtime is (re)applied every call.
    ///
    /// The historical mtime matters twice: Windows "Previous Versions" HIDES any
    /// snapshot whose file mtime equals the live file's, and distinct per-version
    /// mtimes let Explorer tell the versions apart. Returns the share-relative cache
    /// path (forward slashes) the VFS module redirects the open to.
    /// </summary>
    private async Task<string> MaterializeVersionAsync(
        Guid shareId, string sharePath, string gmtToken,
        string cacheScope, FileVersion v)
    {
        if (!ShareRelativePath.IsValid(v.FilePath) ||
            ShareRelativePath.Normalize(v.FilePath).Length == 0)
            throw new InvalidDataException("Version contains an invalid file path.");

        string normalizedPath = ShareRelativePath.Normalize(v.FilePath);
        string rel = $"{CacheDirName}/{gmtToken}/{cacheScope}/{normalizedPath}";
        string scopeRoot = Path.Combine(
            sharePath, CacheDirName, gmtToken, cacheScope);
        string full = GetScopedCachePath(scopeRoot, normalizedPath);

        var existing = new FileInfo(full);
        if (!existing.Exists || existing.Length != v.Size)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await using var content = await _versions.ReadVersionAsync(shareId, v.FilePath, v.SnapshotTimestampUtc);
            await using var outFs = new FileStream(
                full, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await content.CopyToAsync(outFs);
        }

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

    private static void ReconcileUserProjection(
        string sharePath,
        string gmtToken,
        string cacheScope,
        string folderPath,
        IReadOnlyCollection<FileVersion> readableVersions)
    {
        string scopeRoot = Path.Combine(
            sharePath, CacheDirName, gmtToken, cacheScope);
        string normalizedFolder = ShareRelativePath.Normalize(folderPath);
        string folderFull = normalizedFolder.Length == 0
            ? scopeRoot
            : Path.Combine(
                scopeRoot,
                normalizedFolder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(folderFull);

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
            if (!allowed.Contains(Path.GetFullPath(file)))
                File.Delete(file);
        }

        // Remove stale empty directories left behind by revoked files, but retain
        // the requested directory that Samba is about to traverse.
        foreach (string directory in Directory.EnumerateDirectories(
                     folderFull, "*", SearchOption.AllDirectories)
                 .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
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
}
