using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.SmbBridge.Grpc;

namespace Kaimo_File_Server.SmbBridge.Services;

public partial class SnapshotGrpcService
{
    /// <summary>
    /// No file version at the token: the path may be a directory of the historical tree.
    /// Materializes the ACL-filtered folder snapshot into the user's cache projection and
    /// returns it, or not-found when nothing was versioned below the path.
    /// </summary>
    private async Task<ResolveVersionReply> ResolveHistoricalDirectoryAsync(
        ResolveVersionRequest request, UserContext user, ShareDefinition share,
        string normalized, DateTime ts, CancellationToken requestCancellation)
    {
        var notFound = new ResolveVersionReply { Found = false };

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
                .CreateForShare(share)
                .GetFolderSnapshotAsync(
                    normalized, ts, user, folderCancellation);
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
                try { Directory.SetLastWriteTimeUtc(dirFull, ts); }
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
}
