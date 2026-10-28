using System.Globalization;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

public partial class BackupCatalogService
{
    // ══════════════════════════════════════════
    //  Restore (scoped: RestoreFromBackup / RestoreBackupInPlace per share)
    // ══════════════════════════════════════════

    /// <summary>
    /// The homes share is Global-only, like its backup jobs, and additionally needs
    /// <see cref="ManagementPermission.AccessHomeContents"/> (<paramref name="homes"/>).
    /// </summary>
    private static bool CanSeeRestoreShare(BackupAccess access, ShareDefinition share, bool homes)
        => share.IsUserHomes
            ? access.RestoreUnrestricted && homes
            : access.RestoreUnrestricted || access.RestoreShareIds?.Contains(share.Id) == true;

    private async Task<bool> HasShareBitAsync(UserContext actor, ShareDefinition share, ManagementPermission bit)
    {
        var scope = await auth.GetAuthorizedShareIdsAsync(actor, bit);
        return scope.IsUnrestricted || (!share.IsUserHomes && scope.ScopeIds.Contains(share.Id));
    }

    /// <summary>Shares with cached backup points the actor may restore from.</summary>
    public async Task<IReadOnlyList<BackupRestoreShare>> ListRestorableSharesAsync(UserContext actor)
    {
        var access = await GetAccessAsync(actor);
        if (!access.CanRestore)
            return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var points = (await db.BackupSnapshots.AsNoTracking()
                .Where(s => s.Kind == BackupSnapshotKind.Share && s.ShareId != null)
                .Select(s => new { ShareId = s.ShareId!.Value, s.TimeUtc })
                .ToListAsync())
            .GroupBy(s => s.ShareId)
            .ToDictionary(g => g.Key, g => (Count: g.Count(), Latest: g.Max(s => s.TimeUtc)));
        var ids = points.Keys.ToList();
        var shares = await db.ShareDefinitions.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync();
        var homes = await CanSeeHomesAsync(actor);
        return shares.Where(s => CanSeeRestoreShare(access, s, homes))
            .Select(s => new BackupRestoreShare(s.Id, s.Name, points[s.Id].Count, points[s.Id].Latest, s.IsUserHomes))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>All backup points of a share, newest first.</summary>
    public async Task<IReadOnlyList<BackupPointView>> ListBackupPointsAsync(UserContext actor, Guid shareId)
    {
        var access = await GetAccessAsync(actor);
        await using var db = await dbFactory.CreateDbContextAsync();
        var share = await db.ShareDefinitions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == shareId)
                    ?? throw new ResticException("share_missing", "The share no longer exists.");
        if (!CanSeeRestoreShare(access, share, share.IsUserHomes && await CanSeeHomesAsync(actor)))
            throw new UnauthorizedAccessException("The share is outside the actor's restore scope.");
        var snapshots = (await db.BackupSnapshots.AsNoTracking()
                .Where(s => s.ShareId == shareId && s.Kind == BackupSnapshotKind.Share)
                .ToListAsync())
            .OrderByDescending(s => s.TimeUtc).ToList();
        var repoNames = await db.BackupRepositories.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name);
        var jobNames = await db.BackupJobs.AsNoTracking().ToDictionaryAsync(j => j.Id, j => j.Name);
        return snapshots.Select(s => new BackupPointView(
            s.RepositoryId, repoNames.GetValueOrDefault(s.RepositoryId) ?? "?", s.SnapshotId, s.TimeUtc,
            s.JobId is { } jobId ? jobNames.GetValueOrDefault(jobId) : null, s.FileCount, s.TotalBytes)).ToList();
    }

    /// <summary>Which restore bits the actor holds on a share (drives the restore dialog; enforced on enqueue).</summary>
    public async Task<(bool FromBackup, bool InPlace)> GetRestoreRightsAsync(UserContext actor, Guid shareId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var share = await db.ShareDefinitions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == shareId);
        if (share is null)
            return (false, false);
        return (await HasShareBitAsync(actor, share, ManagementPermission.RestoreFromBackup),
            await HasShareBitAsync(actor, share, ManagementPermission.RestoreBackupInPlace));
    }

    /// <summary>Restore targets for "into another share": shares with RestoreBackupInPlace.</summary>
    public async Task<IReadOnlyList<ShareDefinition>> ListRestoreTargetSharesAsync(UserContext actor)
    {
        var scope = await auth.GetAuthorizedShareIdsAsync(actor, ManagementPermission.RestoreBackupInPlace);
        if (!scope.IsUnrestricted && scope.ScopeIds.Count == 0)
            return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var shares = await db.ShareDefinitions.AsNoTracking().OrderBy(s => s.Name).ToListAsync();
        var homes = scope.IsUnrestricted && await CanSeeHomesAsync(actor);
        return shares.Where(s => s.IsUserHomes ? homes : scope.IsUnrestricted || scope.ScopeIds.Contains(s.Id)).ToList();
    }

    /// <summary>Loads a share backup point the actor may see; optionally requires a restore bit on its share.</summary>
    private async Task<(BackupSnapshot Snapshot, ShareDefinition Share, BackupRepository Repository)> AuthorizeBackupPointAsync(
        UserContext actor, Guid repositoryId, string snapshotId, ManagementPermission? required, CancellationToken ct)
    {
        var access = await GetAccessAsync(actor);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var snapshot = await db.BackupSnapshots.AsNoTracking()
                           .FirstOrDefaultAsync(s => s.RepositoryId == repositoryId && s.SnapshotId == snapshotId, ct)
                       ?? throw new ResticException("snapshot_missing", "The backup point no longer exists.");
        var share = snapshot.Kind == BackupSnapshotKind.Share && snapshot.ShareId is { } shareId
            ? await db.ShareDefinitions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == shareId, ct)
            : null;
        if (share is null || !CanSeeRestoreShare(access, share, share.IsUserHomes && await CanSeeHomesAsync(actor))
                          || required is { } bit && !await HasShareBitAsync(actor, share, bit))
            throw new UnauthorizedAccessException("The backup point is outside the actor's restore scope.");
        var repository = await db.BackupRepositories.AsNoTracking().FirstAsync(r => r.Id == repositoryId, ct);
        return (snapshot, share, repository);
    }

    private static string RequireFolder(string? folder, int rootDepth = 0)
        => ShareRelativePath.TryNormalizeStrict(folder ?? string.Empty, out var normalized, rootDepth: rootDepth)
            ? normalized
            : throw new ResticException("path_invalid", "The path is not a valid share path.");

    /// <summary>Lists one folder of a backup point (reads the repository, interactive timeout).</summary>
    public async Task<IReadOnlyList<BackupBrowseEntry>> BrowseAsync(
        UserContext actor, Guid repositoryId, string snapshotId, string folder, CancellationToken ct = default)
    {
        var (snapshot, share, repository) = await AuthorizeBackupPointAsync(actor, repositoryId, snapshotId, null, ct);
        var relative = RequireFolder(folder);
        if (share.IsUserHomes && relative.Length > 0)
            await RequireHomePathAsync(actor, relative);
        var entries = await ListSnapshotFolderAsync(snapshot, repository, relative, ct);
        return share.IsUserHomes && relative.Length == 0 ? await FilterHomesRootAsync(actor, entries) : entries;
    }

    /// <summary>The entries of a normalized folder of a backup point, unfiltered by home owners.</summary>
    private async Task<IReadOnlyList<BackupBrowseEntry>> ListSnapshotFolderAsync(
        BackupSnapshot snapshot, BackupRepository repository, string folder, CancellationToken ct)
    {
        var root = BackupRestoreExecutor.SourceRoot(snapshot)
                   ?? throw new ResticException("snapshot_missing", "The backup point records no share folder.");
        var path = BackupRestoreExecutor.SnapshotPath(root, folder);
        var nodes = await WithInteractiveTimeoutAsync(async token =>
        {
            using var target = await resolver.ResolveAsync(repository, token);
            return await restic.ListDirectoryAsync(target, snapshot.SnapshotId, path, token);
        }, ct);
        return nodes
            .Where(n => !n.Name.StartsWith(ShareEntryPolicy.InternalNamespacePrefix, StringComparison.Ordinal))
            .OrderByDescending(n => n.IsDirectory).ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(n => new BackupBrowseEntry(n.Name, n.IsDirectory, n.Size, n.ModifiedUtc))
            .ToList();
    }

    /// <summary>
    /// Authorizes and queues a restore. New folder needs RestoreFromBackup on the source share;
    /// in place RestoreBackupInPlace; another share RestoreFromBackup on the source and
    /// RestoreBackupInPlace on the target. Backed-up permissions are always restored onto the
    /// paths the restore creates. Every home folder read or written must be reachable
    /// (AccessHomeContents); a new folder at the homes root would be no home and is refused.
    /// </summary>
    public async Task<Guid> EnqueueRestoreAsync(UserContext actor, BackupRestoreRequest request, CancellationToken ct = default)
    {
        RequireWritable();
        if (request.Mode is not (BackupRestoreMode.NewFolder or BackupRestoreMode.InPlace or BackupRestoreMode.OtherShare))
            throw new ResticException("request_invalid", "Unknown restore mode.");
        var sourceBit = request.Mode == BackupRestoreMode.InPlace
            ? ManagementPermission.RestoreBackupInPlace
            : ManagementPermission.RestoreFromBackup;
        var (snapshot, share, repository) = await AuthorizeBackupPointAsync(actor, request.RepositoryId, request.SnapshotId, sourceBit, ct);
        if (request.ShareId != share.Id)
            throw new ResticException("snapshot_missing", "The backup point does not belong to the share.");

        var folder = RequireFolder(request.FolderPath);
        var items = request.Items.Distinct(StringComparer.Ordinal).ToList();
        if (items.Any(i => string.IsNullOrEmpty(i) || i.Contains('/') || i.Contains('\\') || i is "." or ".."
                           || i.StartsWith(ShareEntryPolicy.InternalNamespacePrefix, StringComparison.Ordinal)))
            throw new ResticException("path_invalid", "Only entries of the selected folder can be restored.");
        if (share.IsUserHomes && request.Mode == BackupRestoreMode.NewFolder && folder.Length == 0)
            throw new ResticException("homes_root_new_folder", "A new folder cannot be created at the homes root.");
        if (share.IsUserHomes)
            items = await RequireHomeOwnersAsync(actor, snapshot, repository, folder, items, ct);

        var destination = share;
        string? targetFolder = null;
        if (request.Mode == BackupRestoreMode.OtherShare)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            destination = await db.ShareDefinitions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.TargetShareId, ct)
                          ?? throw new ResticException("share_missing", "The target share no longer exists.");
            if (!await HasShareBitAsync(actor, destination, ManagementPermission.RestoreBackupInPlace))
                throw new UnauthorizedAccessException("The target share is outside the actor's restore scope.");
            targetFolder = RequireFolder(request.TargetFolder, destination.RecycleRootDepth);
            if (targetFolder.Length > 0 && ShareEntryPolicy.IsReservedForUserWrites(targetFolder, destination.RecycleRootDepth))
                throw new ResticException("path_invalid", "The restore target lies in a folder reserved by Kaimo.");
            if (destination.IsUserHomes)
                await RequireHomePathAsync(actor, targetFolder);
            if (!Directory.Exists(ShareRelativePath.ToContainedAbsolutePath(destination.Path, targetFolder, allowInternalNamespace: false)))
                throw new ResticException("target_folder_missing", "The target folder does not exist.");
        }
        var normalized = request with
        {
            FolderPath = folder,
            Items = items,
            // Backed-up permissions are always restored onto the created paths (a copy must not
            // inherit wider access than the original had).
            RestorePermissions = true,
            Conflict = request.Mode == BackupRestoreMode.InPlace ? request.Conflict : BackupRestoreConflict.Skip,
            TargetShareId = request.Mode == BackupRestoreMode.OtherShare ? destination.Id : null,
            TargetFolder = targetFolder,
            // The circuit's UI language; the background run has none of its own.
            Culture = CultureInfo.CurrentUICulture.Name,
        };
        var title = string.Format(CultureInfo.CurrentCulture,
            Resources.ResourceManager.GetString("Web_Backup_Restore_JobTitle") ?? "Restore {0}", share.Name);
        logger.LogInformation("Restore from backup point {Snapshot} of share {Share} ({Mode}) requested by {User}.",
            request.SnapshotId, share.Name, request.Mode, actor.User.Username);
        return await runner.EnqueueRestoreAsync(normalized, title, actor.User.Id, ct);
    }

    /// <summary>
    /// Authorizes a download (RestoreFromBackup) of one file or folder and returns a writer that
    /// streams it straight from the repository. Every download is recorded as a restore run.
    /// </summary>
    public async Task<BackupDownload> OpenDownloadAsync(
        UserContext actor, Guid repositoryId, string snapshotId, string path, CancellationToken ct = default)
    {
        if (demo.ReadOnly)
            throw new ReadOnlyDemoException();
        var (snapshot, share, repository) = await AuthorizeBackupPointAsync(
            actor, repositoryId, snapshotId, ManagementPermission.RestoreFromBackup, ct);
        var relative = RequireFolder(path);
        if (relative.Length == 0)
            throw new ResticException("path_invalid", "Select a file or folder.");
        if (share.IsUserHomes)
            await RequireHomePathAsync(actor, relative);
        var root = BackupRestoreExecutor.SourceRoot(snapshot)
                   ?? throw new ResticException("snapshot_missing", "The backup point records no share folder.");
        var parent = ShareRelativePath.GetParent(relative);
        var name = ShareRelativePath.GetFileName(relative);
        var node = (await BrowseAsync(actor, repositoryId, snapshotId, parent, ct)).FirstOrDefault(e => e.Name == name)
                   ?? throw new ResticException("item_missing", "The entry does not exist in the backup point.");
        var absolute = BackupRestoreExecutor.SnapshotPath(root, relative);
        var details = new BackupRestoreDetails(
            new BackupRestoreRequest(repository.Id, snapshotId, share.Id, parent, [name], BackupRestoreMode.Download, BackupRestoreConflict.Skip, false),
            new BackupRestoreOutcome { SourceShareName = share.Name, DestinationPath = relative });

        return new BackupDownload(node.IsDirectory ? name + ".zip" : name, node.IsDirectory, async (output, token) =>
        {
            var run = new BackupRun
            {
                RepositoryId = repository.Id,
                Type = BackupRunType.Restore,
                Trigger = BackupRunTrigger.Manual,
                ActorUserId = actor.User.Id,
                Status = BackupRunStatus.Running,
                QueuedAtUtc = time.GetUtcNow().UtcDateTime,
                StartedAtUtc = time.GetUtcNow().UtcDateTime,
                DetailsJson = details.Serialize(),
            };
            await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
            {
                db.BackupRuns.Add(run);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            try
            {
                using var target = await resolver.ResolveAsync(repository, token);
                await restic.DumpAsync(target, snapshotId, absolute, node.IsDirectory, output, token);
                run.Status = BackupRunStatus.Succeeded;
            }
            catch (Exception ex)
            {
                run.Status = token.IsCancellationRequested ? BackupRunStatus.Cancelled : BackupRunStatus.Failed;
                run.ErrorCode = ex is ResticException re ? re.Code : token.IsCancellationRequested ? "cancelled" : "unexpected_error";
                throw;
            }
            finally
            {
                run.FinishedAtUtc = time.GetUtcNow().UtcDateTime;
                await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
                db.BackupRuns.Update(run);
                await db.SaveChangesAsync(CancellationToken.None);
            }
        });
    }

    /// <summary>System, pool and foreign backup points, newest first (Global repository admins).</summary>
    public async Task<IReadOnlyList<BackupServerPointView>> ListServerBackupPointsAsync(UserContext actor)
    {
        var access = await GetAccessAsync(actor);
        if (!access.CanManageRepositories)
            return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var snapshots = (await db.BackupSnapshots.AsNoTracking().Where(s => s.Kind != BackupSnapshotKind.Share).ToListAsync())
            .OrderByDescending(s => s.TimeUtc).ToList();
        var repoNames = await db.BackupRepositories.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name);
        var jobNames = await db.BackupJobs.AsNoTracking().ToDictionaryAsync(j => j.Id, j => j.Name);
        return snapshots.Select(s => new BackupServerPointView(
            s.RepositoryId, repoNames.GetValueOrDefault(s.RepositoryId) ?? "?", s.SnapshotId, s.TimeUtc, s.Kind,
            BackupRestoreExecutor.SourceLabel(s), s.ContentLevel,
            s.JobId is { } jobId ? jobNames.GetValueOrDefault(jobId) : null, s.FileCount, s.TotalBytes)).ToList();
    }

    /// <summary>
    /// Queues the restore of a whole system, pool or foreign backup point into a new folder
    /// below the server's backup folder (Global <see cref="ManagementPermission.ManageBackupRepositories"/>:
    /// a system backup point holds the database dump with every credential).
    /// </summary>
    public async Task<Guid> EnqueueServerRestoreAsync(UserContext actor, Guid repositoryId, string snapshotId, CancellationToken ct = default)
    {
        await RequireRepositoriesAsync(actor);
        RequireWritable();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var snapshot = await db.BackupSnapshots.AsNoTracking()
                           .FirstOrDefaultAsync(s => s.RepositoryId == repositoryId && s.SnapshotId == snapshotId, ct)
                       ?? throw new ResticException("snapshot_missing", "The backup point no longer exists.");
        if (snapshot.Kind == BackupSnapshotKind.Share)
            throw new ResticException("request_invalid", "Share backup points are restored into their share.");
        var source = BackupRestoreExecutor.SourceLabel(snapshot);
        var request = new BackupRestoreRequest(repositoryId, snapshotId, Guid.Empty, string.Empty, [],
            BackupRestoreMode.ServerFolder, BackupRestoreConflict.Skip, false, Culture: CultureInfo.CurrentUICulture.Name);
        var title = string.Format(CultureInfo.CurrentCulture,
            Resources.ResourceManager.GetString("Web_Backup_Restore_JobTitle") ?? "Restore {0}", source);
        logger.LogWarning("Restore of backup point {Snapshot} ({Source}) into the server backup folder requested by {User}.",
            snapshotId, source, actor.User.Username);
        return await runner.EnqueueRestoreAsync(request, title, actor.User.Id, ct);
    }
}
