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
/// Thin facade over the already-complete <see cref="IFileVersionService"/>; no
/// versioning logic is duplicated. ACLs are honored so a user only sees/reads the
/// snapshots of paths they may read (<see cref="FilePermission.ListReadData"/>),
/// matching live-file semantics.
/// </summary>
public sealed class SnapshotGrpcService : SnapshotService.SnapshotServiceBase
{
    // Same format Windows expects and FileVersion.ToGmtToken() emits.
    private const string GmtFormat = "'@GMT-'yyyy.MM.dd-HH.mm.ss";

    // Hidden per-share directory that holds materialized (decompressed) versions.
    // Lives INSIDE the share directory so Samba's path validation accepts the
    // redirect (no wide-link/outside-share issues). Hidden from listings by the
    // VFS readdir filter (names starting with ".kaimo-").
    private const string CacheDirName = ".kaimo-snapshots";

    private readonly IShareRepository _shares;
    private readonly IAuthenticationLookup _auth;
    private readonly IAclService _acl;
    private readonly IFileVersionService _versions;
    private readonly ILogger<SnapshotGrpcService> _logger;

    public SnapshotGrpcService(
        IShareRepository shares,
        IAuthenticationLookup auth,
        IAclService acl,
        IFileVersionService versions,
        ILogger<SnapshotGrpcService> logger)
    {
        _shares = shares;
        _auth = auth;
        _acl = acl;
        _versions = versions;
        _logger = logger;
    }

    public override async Task<EnumerateSnapshotsReply> EnumerateSnapshots(
        EnumerateSnapshotsRequest request, ServerCallContext context)
    {
        var reply = new EnumerateSnapshotsReply();

        var user = await _auth.ResolveUserContextAsync(request.Username);
        var share = await _shares.GetByNameAsync(request.Share);
        if (user is null || share is null)
        {
            _logger.LogInformation(
                "EnumerateSnapshots: unknown user/share (user={User} share={Share}) -> 0",
                request.Username, request.Share);
            return reply;
        }

        string normalized = ShareRelativePath.Normalize(request.Path);

        // Don't leak version history of a path the user may not read.
        if (normalized.Length > 0 &&
            !await _acl.HasAccessAsync(user, share.Id, normalized, false, FilePermission.ListReadData))
        {
            _logger.LogInformation(
                "EnumerateSnapshots DENY: user={User} share={Share} path=[{Path}] -> ListReadData",
                request.Username, request.Share, request.Path);
            return reply;
        }

        // A concrete file -> its own version timestamps; a folder or the share
        // root ("") -> all snapshot timestamps under that prefix.
        List<DateTime> timestamps;
        if (normalized.Length > 0)
        {
            var fileVersions = await _versions.GetVersionsAsync(share.Id, normalized);
            timestamps = fileVersions.Count > 0
                ? fileVersions.Select(v => v.SnapshotTimestampUtc).ToList()
                : await _versions.GetSnapshotTimestampsAsync(share.Id, normalized);
        }
        else
        {
            timestamps = await _versions.GetSnapshotTimestampsAsync(share.Id, "");
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

        var user = await _auth.ResolveUserContextAsync(request.Username);
        var share = await _shares.GetByNameAsync(request.Share);
        if (user is null || share is null)
            return notFound;

        string normalized = ShareRelativePath.Normalize(request.Path);

        // Reading a version is a read of the file -> ListReadData parity.
        if (!await _acl.HasAccessAsync(user, share.Id, normalized, false, FilePermission.ListReadData))
        {
            _logger.LogInformation(
                "ResolveVersion DENY: user={User} share={Share} path=[{Path}] -> ListReadData",
                request.Username, request.Share, request.Path);
            return notFound;
        }

        var ts = FileVersion.ParseGmtToken(request.GmtToken);
        if (ts is null)
        {
            _logger.LogWarning("ResolveVersion: bad @GMT token '{Token}'", request.GmtToken);
            return notFound;
        }

        var version = await _versions.GetVersionAtAsync(share.Id, normalized, ts.Value);

        // Directory component of a snapshot path. SMB resolves EVERY parent directory
        // with the timewarp too (smbd stats "weqr" before opening "weqr/file"). Those
        // have no file version, so without special handling the whole open fails at the
        // parent with OBJECT_NAME_NOT_FOUND. We hand back a real (empty) cache directory
        // mirroring the historical tree so traversal reaches the versioned file below.
        // A path is a historical directory if any versioned file existed under it at or
        // before the snapshot time (or it's the share root).
        if (version is null)
        {
            var under = await _versions.GetFolderSnapshotAsync(share.Id, normalized, ts.Value);
            if (normalized.Length == 0 || under.Count > 0)
            {
                string dirRel = normalized.Length == 0
                    ? $"{CacheDirName}/{request.GmtToken}"
                    : $"{CacheDirName}/{request.GmtToken}/{normalized}";
                string dirFull = Path.Combine(share.Path, dirRel.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    Directory.CreateDirectory(dirFull);
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
                            await MaterializeVersionAsync(share.Id, share.Path, request.GmtToken, fv);
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
            return notFound;
        }

        // A concrete versioned file: materialize its content (decompressed) with the
        // historical mtime and hand back the in-share cache path.
        string cacheRel;
        try
        {
            cacheRel = await MaterializeVersionAsync(share.Id, share.Path, request.GmtToken, version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ResolveVersion: materialize failed for share={Share} path=[{Path}] token={Token}",
                request.Share, request.Path, request.GmtToken);
            return notFound;
        }

        _logger.LogInformation(
            "ResolveVersion: user={User} share={Share} path=[{Path}] token={Token} -> {Cache} ({Size} B)",
            request.Username, request.Share, request.Path, request.GmtToken, cacheRel, version.Size);

        return new ResolveVersionReply
        {
            Found = true,
            CachePath = cacheRel,
            Size = version.Size,
        };
    }

    /// <summary>
    /// Writes one version's decompressed content into the in-share snapshot cache
    /// (<c>&lt;share&gt;/.kaimo-snapshots/&lt;@GMT&gt;/&lt;filePath&gt;</c>) and stamps
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
        Guid shareId, string sharePath, string gmtToken, FileVersion v)
    {
        string rel = $"{CacheDirName}/{gmtToken}/{v.FilePath}";
        string full = Path.Combine(sharePath, rel.Replace('/', Path.DirectorySeparatorChar));

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
}
