using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// Applies the global <see cref="VersioningPolicy"/> to every version history, not only to
/// files that are written again. One pass:
/// <list type="number">
/// <item>removes rows of legacy (<see cref="Guid.Empty"/>) and deleted shares;</item>
/// <item>removes the history of files that no longer exist (missed cleanups, deletions
/// outside the server) once they have been missing for <see cref="OrphanGrace"/>;</item>
/// <item>applies thinning, age and count rules (<see cref="VersionRetentionPlanner"/>);</item>
/// <item>brings the older versions of every pool back under
/// <see cref="VersioningPolicy.PoolBudgetPercent"/> by removing the oldest first; the newest
/// version of a file is neither counted nor removed.</item>
/// </list>
/// Run by <see cref="VersionStorageReconcilerService"/> in the Host only (single owner).
/// </summary>
public sealed class VersionRetentionSweeper(
    IFileVersionRepository versions,
    FileVersionService versionService,
    IShareRepository shares,
    IVersioningPolicyProvider policyProvider,
    IVersionStorageLocator locator,
    ILogger<VersionRetentionSweeper> logger,
    TimeProvider? time = null,
    Func<string, (long Free, long Total)>? diskSpaceProbe = null,
    int pageSize = 500)
{
    /// <summary>
    /// A missing file's history survives this long after a sweep first finds the file
    /// missing, so deletions outside the server stay recoverable for a while.
    /// </summary>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromDays(7);

    // Mass orphaning looks like a mount problem rather than a real deletion: hold off.
    private const int MassOrphanMinPaths = 100;
    private const double MassOrphanRatio = 0.9;
    private const int BudgetBatchLimit = 500;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<string, (long Free, long Total)> _diskSpace = diskSpaceProbe ?? ProbeDiskSpace;

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var policy = await policyProvider.GetAsync();
        int foreign = 0, orphans = 0, retained = 0;

        foreach (var shareId in await versions.GetShareIdsWithVersionsAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            var share = shareId == Guid.Empty ? null : await shares.GetByIdAsync(shareId);
            if (share is null)
            {
                foreign += await versionService.DeleteShareAsync(shareId);
                await versions.ReplaceMissingSinceAsync(shareId, new Dictionary<string, DateTime>(), ct);
                continue;
            }

            var (o, r) = await SweepShareAsync(share, policy, ct);
            orphans += o;
            retained += r;
        }

        var budget = policy.PoolBudgetPercent > 0 ? await EnforceBudgetsAsync(policy, ct) : 0;

        if (foreign + orphans + retained + budget > 0)
            logger.LogInformation(
                "Version retention sweep removed {Foreign} versions of deleted shares, {Orphans} of missing files, " +
                "{Retention} by retention rules and {Budget} to meet pool budgets.",
                foreign, orphans, retained, budget);
    }

    private async Task<(int Orphans, int Retention)> SweepShareAsync(
        ShareDefinition share, VersioningPolicy policy, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var rootUsable = IsShareRootUsable(share.Path);
        // Orphans wait for the mass-orphan check at the end; retention deletes page by page,
        // so neither list grows with the size of the share.
        var orphanIds = new List<Guid>();
        var orphanedPaths = new List<string>();
        int scannedPaths = 0, retention = 0;
        // When each file was first found missing, persisted so a restart keeps the clock.
        // Rebuilt every pass: paths found again (or skipped with an unusable root) start over.
        var previouslyMissing = await versions.GetMissingSinceAsync(share.Id, ct);
        var stillMissing = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        string? cursor = null;
        while (true)
        {
            var page = await versions.GetVersionedPathsPageAsync(share.Id, cursor, pageSize, ct);
            if (page.Count == 0) break;
            cursor = page[^1];

            var histories = (await versions.GetVersionsForPathsAsync(share.Id, page, ct))
                .ToLookup(v => v.FilePath, StringComparer.Ordinal);
            var retentionIds = new List<Guid>();
            foreach (var path in page)
            {
                ct.ThrowIfCancellationRequested();
                var history = histories[path].ToList();
                if (history.Count == 0) continue;
                scannedPaths++;

                if (rootUsable && !FileExists(share.Path, path))
                {
                    var since = previouslyMissing.GetValueOrDefault(path, now);
                    stillMissing[path] = since;
                    // The snapshot check covers a file recreated and removed again between passes.
                    if (since <= now - OrphanGrace && history.Max(v => v.SnapshotTimestampUtc) < now - OrphanGrace)
                    {
                        orphanedPaths.Add(path);
                        orphanIds.AddRange(history.Select(v => v.Id));
                        continue;
                    }
                }

                retentionIds.AddRange(VersionRetentionPlanner.SelectForDeletion(history, policy, now).Select(v => v.Id));
            }

            retention += await DeleteInBatchesAsync(retentionIds);
            if (page.Count < pageSize) break;
        }

        var orphanPaths = orphanedPaths.Count;
        if (orphanPaths >= MassOrphanMinPaths && orphanPaths >= scannedPaths * MassOrphanRatio)
        {
            logger.LogWarning(
                "{Orphans} of {Scanned} versioned files of share {Share} are missing on disk; their history is kept " +
                "because this looks like an unavailable volume rather than deletions.",
                orphanPaths, scannedPaths, share.Name);
            orphanIds.Clear();
        }
        else
        {
            // Their history goes now, so there is nothing left to wait for.
            foreach (var path in orphanedPaths) stillMissing.Remove(path);
        }

        var orphans = await DeleteInBatchesAsync(orphanIds);
        await versions.ReplaceMissingSinceAsync(share.Id, stillMissing, ct);
        return (orphans, retention);
    }

    /// <summary>Deletes at most <c>pageSize</c> rows per call: one bounded context and transaction each.</summary>
    private async Task<int> DeleteInBatchesAsync(List<Guid> ids)
    {
        var removed = 0;
        foreach (var chunk in ids.Chunk(pageSize))
            removed += await versionService.DeleteVersionsAsync(chunk);
        return removed;
    }

    /// <summary>
    /// Orphan detection needs a present, non-empty share root: an unmounted pool often
    /// leaves an empty mount point behind, which would make every file look deleted.
    /// </summary>
    private static bool IsShareRootUsable(string sharePath)
    {
        try
        {
            return Directory.Exists(sharePath) && Directory.EnumerateFileSystemEntries(sharePath).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the versioned file still exists. SMB clients are case-insensitive, so a
    /// case-only rename that bypassed the history move must not count as a deletion.
    /// </summary>
    private static bool FileExists(string sharePath, string relativePath)
    {
        var full = Path.Combine(sharePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(full)) return true;

        try
        {
            var parent = Path.GetDirectoryName(full);
            var name = Path.GetFileName(full);
            return parent is not null && Directory.Exists(parent)
                && Directory.EnumerateFiles(parent)
                    .Any(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not proof of absence.
            return true;
        }
    }

    // ------------------ Pool budget ------------------

    private async Task<int> EnforceBudgetsAsync(VersioningPolicy policy, CancellationToken ct)
    {
        var removed = 0;
        var shareIds = await versions.GetShareIdsWithVersionsAsync(ct);
        foreach (var root in locator.AllRoots.Distinct())
        {
            if (!Directory.Exists(root)) continue;

            var members = new List<Guid>();
            foreach (var id in shareIds)
                if (await locator.GetRootAsync(id) is { } shareRoot && SamePath(shareRoot, root))
                    members.Add(id);
            if (members.Count == 0) continue;

            removed += await EnforceBudgetAsync(root, members, policy, ct);
        }
        return removed;
    }

    private async Task<int> EnforceBudgetAsync(
        string root, List<Guid> shareIds, VersioningPolicy policy, CancellationToken ct)
    {
        var budget = (long)(_diskSpace(root).Total * (policy.PoolBudgetPercent / 100.0));

        // Only history counts: the newest version of a file duplicates live content and can
        // never be pruned, and unreferenced leftovers are reconciliation's job. Counting
        // either would prune every history once the live data alone exceeds the budget.
        var historyBlobs = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var storagePath in await versions.GetHistoryOnlyStoragePathsAsync(shareIds, ct))
            historyBlobs[storagePath] = BlobSize(root, storagePath);
        var used = historyBlobs.Values.Sum();
        var removed = 0;

        while (used > budget)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = await versions.GetBudgetPruneCandidatesAsync(shareIds, BudgetBatchLimit, ct);
            if (candidates.Count == 0)
            {
                logger.LogWarning(
                    "Version storage {Root} uses {Used} bytes, above its budget of {Budget} bytes, but only the newest " +
                    "version of each file is left.", root, used, budget);
                break;
            }

            // Oldest first, only as many as the excess needs (shared blobs may free less).
            var batch = new List<Guid>();
            var touched = new HashSet<string>(StringComparer.Ordinal);
            long expected = 0;
            foreach (var candidate in candidates)
            {
                batch.Add(candidate.Id);
                if (touched.Add(candidate.StoragePath))
                    expected += historyBlobs.GetValueOrDefault(candidate.StoragePath);
                if (expected >= used - budget) break;
            }

            removed += await DeleteInBatchesAsync(batch);
            foreach (var storagePath in touched)
            {
                if (historyBlobs.TryGetValue(storagePath, out var size) && !File.Exists(Path.Combine(root, storagePath)))
                {
                    used -= size;
                    historyBlobs.Remove(storagePath);
                }
            }
        }

        return removed;
    }

    private static long BlobSize(string root, string storagePath)
    {
        var info = new FileInfo(Path.Combine(root, storagePath));
        return info.Exists ? info.Length : 0;
    }

    private static (long Free, long Total) ProbeDiskSpace(string path)
    {
        var drive = new DriveInfo(path);
        return (drive.AvailableFreeSpace, drive.TotalSize);
    }

    private static bool SamePath(string a, string b)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
