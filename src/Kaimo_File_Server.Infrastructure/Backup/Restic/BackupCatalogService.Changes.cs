using System.Collections.Concurrent;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

public partial class BackupCatalogService
{
    // ══════════════════════════════════════════
    //  Changes of a backup point (same scope as browsing it)
    // ══════════════════════════════════════════

    /// <summary>Changes listed per comparison; the counts always cover all of them.</summary>
    public const int MaximumListedChanges = 5000;

    /// <summary>restic loads both snapshot trees, which takes longer than listing one folder.</summary>
    private static readonly TimeSpan ChangesTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Diffs running at once across all circuits; each loads two whole snapshot trees.</summary>
    public const int MaximumParallelDiffs = 2;

    // ponytail: process-wide cap, per-repository slots if several repositories are compared at once routinely
    private static readonly SemaphoreSlim DiffSlots = new(MaximumParallelDiffs);

    /// <summary>Users with a running comparison: one each, so nobody holds every slot (e.g. with several tabs).</summary>
    private static readonly ConcurrentDictionary<Guid, byte> UsersComparing = new();

    /// <summary>
    /// What changed in a share backup point compared with <paramref name="baseSnapshotId"/>, or by
    /// default with the share's previous backup point in the same repository (restic can only
    /// compare within one repository). Each side is compared at its own share folder, so a share
    /// moved to another pool still compares file by file.
    /// </summary>
    public async Task<BackupChangeSet> GetChangesAsync(
        UserContext actor, Guid repositoryId, string snapshotId, string? baseSnapshotId = null, CancellationToken ct = default)
    {
        var (snapshot, share, repository) = await AuthorizeBackupPointAsync(actor, repositoryId, snapshotId, null, ct);
        var basePoint = await FindBasePointAsync(snapshot, baseSnapshotId, ct);
        if (basePoint is null)
            return new BackupChangeSet(null, null, [], 0, 0, 0);
        var root = BackupRestoreExecutor.SourceRoot(snapshot);
        var baseRoot = BackupRestoreExecutor.SourceRoot(basePoint);
        if (root is null || baseRoot is null)
            throw new ResticException("snapshot_missing", "The backup point records no share folder.");
        if (!UsersComparing.TryAdd(actor.User.Id, 0))
            throw new ResticException("changes_busy", "Another comparison of this user is still running.");
        try
        {
            var homes = share.IsUserHomes ? await ReachableHomesAsync(actor, repository, [basePoint, snapshot], ct) : null;
            var items = new List<BackupChange>();
            int added = 0, modified = 0, removed = 0;
            // Waiting for a free slot counts against the timeout, so a full queue ends as "changes_timeout".
            await WithInteractiveTimeoutAsync(async token =>
            {
                await DiffSlots.WaitAsync(token);
                try
                {
                    using var target = await resolver.ResolveAsync(repository, token);
                    await restic.DiffAsync(target, basePoint.SnapshotId, baseRoot, snapshot.SnapshotId, root, change =>
                    {
                        if (ToChange(change, homes) is not { } item)
                            return;
                        _ = item.Kind switch
                        {
                            BackupChangeKind.Added => added++,
                            BackupChangeKind.Removed => removed++,
                            _ => modified++,
                        };
                        if (items.Count < MaximumListedChanges)
                            items.Add(item);
                    }, token);
                }
                finally
                {
                    DiffSlots.Release();
                }
                return true;
            }, ct, ChangesTimeout);
            return new BackupChangeSet(basePoint.SnapshotId, basePoint.TimeUtc, items, added, modified, removed);
        }
        catch (ResticException ex) when (ex.Code == "timeout")
        {
            // "Check address and credentials" would mislead: a big share or a full queue is the usual cause.
            throw new ResticException("changes_timeout", "The comparison did not finish in time.", ex);
        }
        finally
        {
            UsersComparing.TryRemove(actor.User.Id, out _);
        }
    }

    /// <summary>
    /// The chosen or else the newest older backup point of the same share in the same repository,
    /// preferring the same job: another job may include the recycle bin or versions, which would
    /// show up as changes that are none. Only older points qualify, or "added" and "removed" swap.
    /// </summary>
    private async Task<BackupSnapshot?> FindBasePointAsync(BackupSnapshot snapshot, string? baseSnapshotId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var older = (await db.BackupSnapshots.AsNoTracking()
                .Where(s => s.RepositoryId == snapshot.RepositoryId && s.ShareId == snapshot.ShareId
                            && s.Kind == BackupSnapshotKind.Share && s.SnapshotId != snapshot.SnapshotId)
                .ToListAsync(ct))
            .Where(s => s.TimeUtc < snapshot.TimeUtc)
            .ToList();
        if (baseSnapshotId is not null)
            return older.FirstOrDefault(s => s.SnapshotId == baseSnapshotId)
                   ?? throw new ResticException("snapshot_missing", "The backup point to compare with is no older one of this share.");
        return older.Where(s => s.JobId == snapshot.JobId).MaxBy(s => s.TimeUtc) ?? older.MaxBy(s => s.TimeUtc);
    }

    /// <summary>Home folder names (first path segment) the actor may reach in either backup point.</summary>
    private async Task<HashSet<string>> ReachableHomesAsync(
        UserContext actor, BackupRepository repository, IEnumerable<BackupSnapshot> points, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in points)
            names.UnionWith((await ListSnapshotFolderAsync(point, repository, string.Empty, ct)).Select(e => e.Name));
        var owners = new Dictionary<Guid, bool>();
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        using var scope = auth.BeginEvaluationScope();
        foreach (var name in names)
        {
            if (await MayAccessHomePathAsync(actor, name, owners))
                reachable.Add(name);
        }
        return reachable;
    }

    /// <summary>
    /// Maps one restic change to a share-relative entry; null for the compared folder itself,
    /// Kaimo-internal entries, homes outside <paramref name="homes"/> and metadata-only changes
    /// (restic 0.19 prints those only with <c>--metadata</c>, which Kaimo does not pass).
    /// </summary>
    internal static BackupChange? ToChange(ResticChange change, IReadOnlySet<string>? homes)
    {
        var isDirectory = change.Path.EndsWith('/');
        var path = change.Path.Trim('/');
        if (path.Length == 0)
            return null;
        var segments = path.Split('/');
        if (segments.Any(s => s.StartsWith(ShareEntryPolicy.InternalNamespacePrefix, StringComparison.Ordinal))
            || homes is not null && !homes.Contains(segments[0]))
            return null;
        var modifier = change.Modifier;
        BackupChangeKind? kind = modifier.Contains('+') ? BackupChangeKind.Added
            : modifier.Contains('-') ? BackupChangeKind.Removed
            : modifier.Contains('M') || modifier.Contains('T') ? BackupChangeKind.Modified
            : null;
        return kind is null ? null : new BackupChange(path, kind.Value, isDirectory);
    }
}
