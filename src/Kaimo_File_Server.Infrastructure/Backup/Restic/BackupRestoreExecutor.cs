using System.Globalization;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>
/// Executes one queued restore run. restic restores into a private staging folder inside the
/// destination share (<c>.kaimo-restore</c>, same file system ⇒ atomic moves, invisible to
/// users and excluded from backups); the staged entries are then moved into place under the
/// share lock. In-place restores never delete anything and keep a file version of every file
/// they overwrite. The backed-up owners and ACLs are written onto every path the restore created;
/// paths that already existed keep their live permissions.
/// </summary>
public sealed class BackupRestoreExecutor(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ResticTargetResolver resolver,
    ResticClient restic,
    IFileVersionService versions,
    IFileChangeLog changeLog,
    ShareLockManager locks,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<BackupRestoreExecutor> logger)
{
    public const string StagingFolderName = ShareEntryPolicy.InternalNamespacePrefix + "restore";
    private const double RequiredSpaceFactor = 1.1;

    /// <summary>Where system and pool backup points are restored to (one new folder per restore).</summary>
    public string ServerRestoreRoot => configuration["Backup:Restic:RestoreDirectory"]
        ?? Path.Combine(configuration["Backup:RootPath"] ?? "/data/kaimo-backups", "restic-restore");

    public async Task ExecuteAsync(Guid runId, Action<string?, int> report, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.BackupRuns.FirstAsync(r => r.Id == runId, ct);
        var details = BackupRestoreDetails.TryParse(run.DetailsJson);

        run.Status = BackupRunStatus.Running;
        run.StartedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(CancellationToken.None);

        if (details?.Request.Mode == BackupRestoreMode.ServerFolder)
        {
            await RestoreToServerFolderAsync(db, run, details, report, ct);
            return;
        }

        var outcome = new BackupRestoreOutcome();
        string? staging = null;
        try
        {
            if (details is null)
                throw new ResticException("request_invalid", "The restore request could not be read.");
            var request = details.Request;
            var repository = await db.BackupRepositories.FirstAsync(r => r.Id == run.RepositoryId, ct);
            var snapshot = await db.BackupSnapshots.AsNoTracking()
                               .FirstOrDefaultAsync(s => s.RepositoryId == repository.Id && s.SnapshotId == request.SnapshotId, ct)
                           ?? throw new ResticException("snapshot_missing", "The backup point no longer exists.");
            if (snapshot.ShareId != request.ShareId)
                throw new ResticException("snapshot_missing", "The backup point does not belong to the share.");
            var sourceRoot = SourceRoot(snapshot)
                             ?? throw new ResticException("snapshot_missing", "The backup point records no share folder.");

            var destinationShareId = request.Mode == BackupRestoreMode.OtherShare ? request.TargetShareId!.Value : request.ShareId;
            var destination = await db.ShareDefinitions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == destinationShareId, ct)
                              ?? throw new ResticException("share_missing", "The target share no longer exists.");
            var destinationRoot = Path.TrimEndingDirectorySeparator(destination.Path);
            if (!Directory.Exists(destinationRoot))
                throw new ResticException("target_unavailable", "The target share folder does not exist (pool not mounted?).");

            var folder = NormalizeOrThrow(request.FolderPath, 0);
            var depth = destination.RecycleRootDepth;
            var now = time.GetUtcNow().UtcDateTime;
            var destinationBase = request.Mode switch
            {
                BackupRestoreMode.InPlace => folder,
                BackupRestoreMode.NewFolder => ShareRelativePath.Combine(folder, UniqueRestoredFolderName(destinationRoot, folder, now, request.GetUiCulture())),
                _ => ShareRelativePath.Combine(NormalizeOrThrow(request.TargetFolder, depth),
                    UniqueRestoredFolderName(destinationRoot, NormalizeOrThrow(request.TargetFolder, depth), now, request.GetUiCulture())),
            };
            if (destinationBase.Length > 0 && ShareEntryPolicy.IsReservedForUserWrites(destinationBase, depth))
                throw new ResticException("path_invalid", "The restore target lies in a folder reserved by Kaimo.");
            outcome.SourceShareName = await db.ShareDefinitions.AsNoTracking()
                .Where(s => s.Id == request.ShareId).Select(s => s.Name).FirstOrDefaultAsync(ct);
            outcome.DestinationShareId = destination.Id;
            outcome.DestinationShareName = destination.Name;
            outcome.DestinationPath = destinationBase;

            using var target = await resolver.ResolveAsync(repository, ct);
            var snapshotFolder = SnapshotPath(sourceRoot, folder);
            var items = request.Items.ToHashSet(StringComparer.Ordinal);

            report(destination.Name, 1);
            long required = 0;
            await restic.ListAsync(target, request.SnapshotId, snapshotFolder, recursive: true, node =>
            {
                if (!node.IsDirectory && IsSelected(RelativeTo(node.Path, snapshotFolder), items))
                    required += node.Size;
            }, ct);
            EnsureFreeSpace(destinationRoot, (long)(required * RequiredSpaceFactor));

            staging = Path.Combine(destinationRoot, StagingFolderName, runId.ToString("N"));
            var data = Path.Combine(staging, "data");
            CreatePrivateDirectory(data);
            // Read before anything is restored or moved: a backup point whose permissions cannot be
            // read is refused instead of leaving a copy that only inherits its new parent's ACL.
            var manifest = await ReadManifestAsync(snapshot,
                (manifestPath, file, token) => restic.DumpAsync(target, request.SnapshotId, manifestPath, asZipArchive: false, file, token),
                staging, ct);

            var restored = await restic.RestoreAsync(target, request.SnapshotId, snapshotFolder, request.Items, data,
                p => report(null, 5 + (int)(Math.Clamp(p, 0, 1) * 80)), ct);
            outcome.FilesRestored = restored.FilesRestored;
            outcome.BytesRestored = restored.BytesRestored;
            outcome.SkippedItems.AddRange(request.Items.Where(i => !Path.Exists(Path.Combine(data, i))));

            report(null, 90);
            var merge = new RestoreMerge(destinationRoot, destination.Id, request.Conflict, versions,
                run.ActorUserId?.ToString(), RenameSuffix(now, request.GetUiCulture()), outcome);
            var shareLock = locks.GetLock(destination.Name);
            await shareLock.WaitAsync(ct);
            try
            {
                Directory.CreateDirectory(ShareRelativePath.ToContainedAbsolutePath(destinationRoot, destinationBase, allowInternalNamespace: false));
                foreach (var entry in Directory.EnumerateFileSystemEntries(data).ToList())
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = ShareRelativePath.Combine(destinationBase, Path.GetFileName(entry));
                    if (ShareEntryPolicy.IsReservedForUserWrites(relative, depth))
                    {
                        outcome.SkippedItems.Add(relative);
                        continue;
                    }
                    await merge.MoveAsync(entry, relative);
                }
            }
            finally
            {
                shareLock.Release();
            }

            // One coarse entry: search re-indexes and sync clients reconcile the subtree.
            await AppendChangeAsync(destination.Id, destinationBase);

            // Always (also for requests queued with RestorePermissions = false), but only onto the
            // paths this restore created: an existing item keeps its live ACL, so a grant revoked
            // since the backup does not come back with an in-place restore.
            report(null, 95);
            await ApplyBackedUpPermissionsAsync(dbFactory, manifest, destination.Id, destinationRoot,
                p => MapPath(p, folder, items, destinationBase, merge.Untouched, merge.Existing),
                run.ActorUserId ?? Guid.Empty, now, outcome, ct);

            var warnings = outcome.SkippedItems.Count + outcome.SkippedPrincipals.Count;
            run.Status = warnings > 0 ? BackupRunStatus.SucceededWithWarnings : BackupRunStatus.Succeeded;
            run.WarningCount = warnings;
            run.FilesNew = outcome.FilesRestored;
            run.BytesProcessed = outcome.BytesRestored;
            repository.LastReachableAtUtc = time.GetUtcNow().UtcDateTime;
            report(null, 100);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Status = BackupRunStatus.Cancelled;
            run.ErrorCode = "cancelled";
        }
        catch (Exception ex)
        {
            run.Status = BackupRunStatus.Failed;
            run.ErrorCode = ex is ResticException re ? re.Code : "unexpected_error";
            if (ex.Data["stderr"] is string stderr && stderr.Length > 0)
                run.LogExcerpt = stderr.Length <= 8 * 1024 ? stderr : stderr[^(8 * 1024)..];
            if (ex is not ResticException)
                logger.LogError(ex, "Restore run {RunId} failed unexpectedly.", runId);
        }
        finally
        {
            if (staging is not null)
            {
                TryDeleteDirectory(staging);
                // The shared staging root only disappears when no other restore uses it.
                try { Directory.Delete(Path.GetDirectoryName(staging)!); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        run.FinishedAtUtc = time.GetUtcNow().UtcDateTime;
        if (details is not null)
            run.DetailsJson = (details with { Result = outcome }).Serialize();
        await db.SaveChangesAsync(CancellationToken.None);
        logger.LogInformation("Restore run {RunId} finished: {Status} ({Code}).", runId, run.Status, run.ErrorCode);
    }

    /// <summary>
    /// Restores a whole system, pool or foreign backup point into a new owner-only folder below
    /// <see cref="ServerRestoreRoot"/>; the snapshot's absolute paths are kept below it. The
    /// admin takes the database dump (startup restore runbook) or files from there. A failed or
    /// cancelled restore removes its partial folder.
    /// </summary>
    private async Task RestoreToServerFolderAsync(
        ApplicationDbContext db, BackupRun run, BackupRestoreDetails details, Action<string?, int> report, CancellationToken ct)
    {
        var outcome = new BackupRestoreOutcome();
        string? folder = null;
        try
        {
            var repository = await db.BackupRepositories.FirstAsync(r => r.Id == run.RepositoryId, ct);
            var snapshot = await db.BackupSnapshots.AsNoTracking()
                               .FirstOrDefaultAsync(s => s.RepositoryId == repository.Id && s.SnapshotId == details.Request.SnapshotId, ct)
                           ?? throw new ResticException("snapshot_missing", "The backup point no longer exists.");
            if (snapshot.Kind == BackupSnapshotKind.Share)
                throw new ResticException("request_invalid", "Share backup points are restored into their share.");
            outcome.SourceShareName = SourceLabel(snapshot, details.Request.GetUiCulture());

            CreatePrivateDirectory(ServerRestoreRoot);
            EnsureFreeSpace(ServerRestoreRoot, (long)(snapshot.TotalBytes * RequiredSpaceFactor));
            var local = TimeZoneInfo.ConvertTimeFromUtc(time.GetUtcNow().UtcDateTime, TimeZoneInfo.Local);
            var name = local.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
            var final = Path.Combine(ServerRestoreRoot, name);
            for (var n = 2; Path.Exists(final) || Path.Exists(final + PartialSuffix); n++)
                final = Path.Combine(ServerRestoreRoot, $"{name}-{n}");
            // Restored under a ".partial" name and renamed when complete, so leftovers of a
            // crashed process are recognizable and removed on the next start.
            folder = final + PartialSuffix;
            CreatePrivateDirectory(folder);
            outcome.DestinationPath = final;

            using var target = await resolver.ResolveAsync(repository, ct);
            report(outcome.SourceShareName, 1);
            var restored = await restic.RestoreSnapshotAsync(target, snapshot.SnapshotId, folder,
                p => report(null, 5 + (int)(Math.Clamp(p, 0, 1) * 90)), ct);
            Directory.Move(folder, final);
            folder = null;
            outcome.FilesRestored = restored.FilesRestored;
            outcome.BytesRestored = restored.BytesRestored;
            run.FilesNew = restored.FilesRestored;
            run.BytesProcessed = restored.BytesRestored;
            run.Status = BackupRunStatus.Succeeded;
            repository.LastReachableAtUtc = time.GetUtcNow().UtcDateTime;
            report(null, 100);
            logger.LogWarning("Backup point {Snapshot} restored into server folder {Folder}.", snapshot.SnapshotId, folder);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Status = BackupRunStatus.Cancelled;
            run.ErrorCode = "cancelled";
        }
        catch (Exception ex)
        {
            run.Status = BackupRunStatus.Failed;
            run.ErrorCode = ex is ResticException re ? re.Code : "unexpected_error";
            if (ex.Data["stderr"] is string stderr && stderr.Length > 0)
                run.LogExcerpt = stderr.Length <= 8 * 1024 ? stderr : stderr[^(8 * 1024)..];
            if (ex is not ResticException)
                logger.LogError(ex, "Restore run {RunId} failed unexpectedly.", run.Id);
        }

        if (folder is not null)
            TryDeleteDirectory(folder);
        run.FinishedAtUtc = time.GetUtcNow().UtcDateTime;
        run.DetailsJson = (details with { Result = outcome }).Serialize();
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private const string PartialSuffix = ".partial";

    /// <summary>
    /// Dumps the permission manifest of a share backup point into <paramref name="staging"/> and
    /// reads it. Throws <see cref="ResticException"/> (<c>manifest_missing</c> / <c>manifest_invalid</c>)
    /// when the backup point records none or it cannot be read.
    /// </summary>
    internal static async Task<ManifestDocument> ReadManifestAsync(BackupSnapshot snapshot,
        Func<string, Stream, CancellationToken, Task> dumpManifest, string staging, CancellationToken ct)
    {
        var manifestPath = ManifestPath(snapshot)
                           ?? throw new ResticException("manifest_missing", "The backup point holds no permission manifest.");
        Directory.CreateDirectory(staging);
        var manifestFile = Path.Combine(staging, BackupAclManifest.FileName);
        await using (var file = new FileStream(manifestFile, FileMode.Create, FileAccess.Write))
            await dumpManifest(manifestPath, file, ct);
        await using var manifest = File.OpenRead(manifestFile);
        return await ParseManifestAsync(manifest, ct);
    }

    /// <summary>
    /// Writes the backed-up owners and ACLs of the restored paths (<paramref name="map"/> keeps only
    /// the paths the restore created, mapped onto their destination; unknown principals are skipped).
    /// </summary>
    internal static async Task ApplyBackedUpPermissionsAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, ManifestDocument manifest,
        Guid shareId, string shareRoot, Func<string, string?> map, Guid actorUserId, DateTime nowUtc,
        BackupRestoreOutcome outcome, CancellationToken ct)
    {
        // Own context: ApplyAclAsync clears its change tracker in batches, which would
        // otherwise detach the run's row and lose its final status.
        await using var aclDb = await dbFactory.CreateDbContextAsync(ct);
        var (applied, skipped) = await ApplyAclAsync(aclDb, manifest, shareId, shareRoot, map, actorUserId, nowUtc, ct);
        outcome.AclEntriesApplied = applied;
        outcome.SkippedPrincipals = skipped;
    }

    /// <summary>
    /// Display name of a non-share backup point in <paramref name="culture"/> (default: the
    /// thread's UI culture): "Pool pool01", the system label or the recorded paths.
    /// </summary>
    internal static string SourceLabel(BackupSnapshot snapshot, CultureInfo? culture = null) => snapshot.Kind switch
    {
        BackupSnapshotKind.Pool => BackupJobExecutor.PoolSourceName(snapshot.PoolPath ?? "?", culture),
        BackupSnapshotKind.System => BackupJobExecutor.SystemSourceName(culture),
        _ => string.Join(", ", Paths(snapshot)),
    };

    /// <summary>The database dump inside a system backup point, or null when it records none.</summary>
    internal static string? DatabaseDumpPath(BackupSnapshot snapshot)
        => Paths(snapshot).FirstOrDefault(p => p.EndsWith("/system", StringComparison.Ordinal)) is { } system
            ? system + "/" + BackupJobExecutor.DatabaseDumpFileName
            : null;

    private async Task AppendChangeAsync(Guid shareId, string path)
    {
        try
        {
            await changeLog.AppendAsync(shareId, FileChangeType.SubtreeChanged, path, isDirectory: true);
        }
        catch (Exception ex)
        {
            // Best effort like every change-log write: clients recover through their periodic full reconcile.
            logger.LogWarning(ex, "Could not record the restore in the change log of share {ShareId}.", shareId);
        }
    }

    // ══════════════════════════════════════════
    //  Paths
    // ══════════════════════════════════════════

    /// <summary>The share folder as recorded in the snapshot (every path except the manifest).</summary>
    public static string? SourceRoot(BackupSnapshot snapshot)
        => Paths(snapshot).FirstOrDefault(p => !p.EndsWith("/" + BackupAclManifest.FileName, StringComparison.Ordinal));

    public static string? ManifestPath(BackupSnapshot snapshot)
        => Paths(snapshot).FirstOrDefault(p => p.EndsWith("/" + BackupAclManifest.FileName, StringComparison.Ordinal));

    private static IReadOnlyList<string> Paths(BackupSnapshot snapshot)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(snapshot.PathsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Absolute snapshot path of a share-relative folder.</summary>
    public static string SnapshotPath(string sourceRoot, string relativeFolder)
    {
        var root = ResticClient.TrimDirectory(sourceRoot);
        return relativeFolder.Length == 0 ? root : root.TrimEnd('/') + "/" + relativeFolder;
    }

    /// <summary>Path of <paramref name="absolute"/> below <paramref name="folder"/> ("" for the folder itself, null outside).</summary>
    internal static string? RelativeTo(string absolute, string folder)
    {
        var prefix = folder.TrimEnd('/') + "/";
        return absolute == folder ? string.Empty
            : absolute.StartsWith(prefix, StringComparison.Ordinal) ? absolute[prefix.Length..]
            : null;
    }

    private static bool IsSelected(string? relative, IReadOnlySet<string> items)
        => !string.IsNullOrEmpty(relative) && (items.Count == 0 || items.Contains(relative.Split('/', 2)[0]));

    /// <summary>
    /// Maps a share-relative path of the backed-up share to its restore destination, or null
    /// when it is not part of the selection, was left untouched (skipped / kept by a rename) or
    /// existed before the restore (<paramref name="existing"/>: it keeps its live permissions).
    /// </summary>
    internal static string? MapPath(
        string manifestPath, string folder, IReadOnlySet<string> items, string destinationBase, IReadOnlyCollection<string> untouched,
        IReadOnlySet<string>? existing = null)
    {
        var rest = folder.Length == 0 ? manifestPath : RelativeTo(manifestPath, folder);
        if (!IsSelected(rest, items))
            return null;
        var destination = ShareRelativePath.Combine(destinationBase, rest);
        if (existing?.Contains(destination) == true)
            return null;
        return untouched.Any(u => destination == u || destination.StartsWith(u + "/", StringComparison.Ordinal))
            ? null
            : destination;
    }

    private static string NormalizeOrThrow(string? relative, int rootDepth)
        => ShareRelativePath.TryNormalizeStrict(relative ?? string.Empty, out var normalized, rootDepth: rootDepth)
            ? normalized
            : throw new ResticException("path_invalid", "The path is not a valid share path.");

    internal static string UniqueRestoredFolderName(string shareRoot, string parent, DateTime nowUtc, CultureInfo culture)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneInfo.Local);
        var template = Resources.ResourceManager.GetString("Web_Backup_Restore_FolderName", culture) ?? "Restored {0}";
        var name = string.Format(CultureInfo.InvariantCulture, template, local.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture));
        var directory = ShareRelativePath.ToContainedAbsolutePath(shareRoot, parent, allowInternalNamespace: false);
        var candidate = name;
        for (var n = 2; Path.Exists(Path.Combine(directory, candidate)); n++)
            candidate = $"{name} ({n})";
        return candidate;
    }

    private static string RenameSuffix(DateTime nowUtc, CultureInfo culture)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneInfo.Local);
        var template = Resources.ResourceManager.GetString("Web_Backup_Restore_RenameSuffix", culture) ?? "restored {0}";
        return " (" + string.Format(CultureInfo.InvariantCulture, template, local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) + ")";
    }

    internal static void EnsureFreeSpace(string path, long required)
    {
        long free;
        try { free = new DriveInfo(path).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return; }
        if (free < required)
            throw new ResticException("insufficient_space", "The target has not enough free space for the restore.");
    }

    // ══════════════════════════════════════════
    //  Permissions
    // ══════════════════════════════════════════

    internal sealed record ManifestAce(Guid PrincipalId, string? PrincipalName, int EntryType, long Permissions, int Inheritance);
    internal sealed record ManifestEntry(string Path, bool IsDirectory, Guid OwnerId, string? OwnerName, List<ManifestAce>? Acl);
    internal sealed record ManifestDocument(int FormatVersion, List<ManifestEntry>? Entries);

    /// <summary>
    /// Writes owners and ACL entries of the manifest back onto the restored paths of
    /// <paramref name="shareId"/> (<paramref name="map"/> returns null for paths that are not
    /// restored). Principals that no longer exist are skipped and returned; an unknown owner
    /// keeps the current owner (new rows: the restoring admin).
    /// </summary>
    internal static async Task<(int Applied, List<BackupSkippedPrincipal> Skipped)> ApplyAclAsync(
        ApplicationDbContext db, Stream manifest, Guid shareId, string shareRoot, Func<string, string?> map,
        Guid actorUserId, DateTime nowUtc, CancellationToken ct)
        => await ApplyAclAsync(db, await ParseManifestAsync(manifest, ct), shareId, shareRoot, map, actorUserId, nowUtc, ct);

    private static async Task<ManifestDocument> ParseManifestAsync(Stream manifest, CancellationToken ct)
    {
        // ponytail: whole manifest in memory; switch to a streaming reader for shares with millions of ACL rows.
        ManifestDocument? document;
        try
        {
            document = await JsonSerializer.DeserializeAsync<ManifestDocument>(manifest, BackupRestoreDetails.Json, ct);
        }
        catch (JsonException)
        {
            document = null;
        }
        if (document is null)
            throw new ResticException("manifest_invalid", "The permission manifest could not be read.");
        if (document.FormatVersion > BackupAclManifest.FormatVersion)
            throw new ResticException("manifest_invalid", "The permission manifest was written by a newer version.");
        return document;
    }

    private static async Task<(int Applied, List<BackupSkippedPrincipal> Skipped)> ApplyAclAsync(
        ApplicationDbContext db, ManifestDocument document, Guid shareId, string shareRoot, Func<string, string?> map,
        Guid actorUserId, DateTime nowUtc, CancellationToken ct)
    {
        var users = (await db.Users.AsNoTracking().Select(u => u.Id).ToListAsync(ct)).ToHashSet();
        var principals = new HashSet<Guid>(users);
        principals.UnionWith(await db.Groups.AsNoTracking().Select(g => g.Id).ToListAsync(ct));
        principals.UnionWith(await db.Roles.AsNoTracking().Select(r => r.Id).ToListAsync(ct));

        var skipped = new Dictionary<Guid, BackupSkippedPrincipal>();
        var applied = 0;
        var pending = 0;
        foreach (var entry in document.Entries ?? [])
        {
            var destination = map(entry.Path);
            if (destination is null)
                continue;
            var absolute = ShareRelativePath.ToContainedAbsolutePath(shareRoot, destination, allowInternalNamespace: false);
            if (!Path.Exists(absolute))
                continue;

            var row = await db.FileMetadata.Include(m => m.Acl)
                .FirstOrDefaultAsync(m => m.ShareId == shareId && m.Path == destination, ct);
            var ownerKnown = users.Contains(entry.OwnerId);
            if (!ownerKnown && entry.OwnerId != Guid.Empty)
                skipped.TryAdd(entry.OwnerId, new BackupSkippedPrincipal(entry.OwnerId, entry.OwnerName));
            if (row is null)
            {
                row = new FileMetadata
                {
                    Id = Guid.NewGuid(),
                    ShareId = shareId,
                    Path = destination,
                    Name = ShareRelativePath.GetFileName(destination),
                    IsDirectory = entry.IsDirectory,
                    Size = entry.IsDirectory ? 0 : new FileInfo(absolute).Length,
                    CreatedAt = nowUtc,
                    ModifiedAt = nowUtc,
                    OwnerId = ownerKnown ? entry.OwnerId : actorUserId,
                };
                db.FileMetadata.Add(row);
            }
            else if (ownerKnown)
            {
                row.OwnerId = entry.OwnerId;
            }

            db.AccessEntries.RemoveRange(row.Acl);
            row.Acl.Clear();
            foreach (var ace in entry.Acl ?? [])
            {
                if (!principals.Contains(ace.PrincipalId))
                {
                    skipped.TryAdd(ace.PrincipalId, new BackupSkippedPrincipal(ace.PrincipalId, ace.PrincipalName));
                    continue;
                }
                // Added explicitly: the entry carries its own key, which EF would otherwise read
                // as an existing row reached through the navigation (UPDATE of 0 rows).
                db.AccessEntries.Add(new AccessEntry(ace.PrincipalId, (AclEntryType)ace.EntryType,
                    (FilePermission)ace.Permissions, (AclInheritance)ace.Inheritance) { FileMetadataId = row.Id });
                applied++;
            }

            if (++pending >= 200)
            {
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                pending = 0;
            }
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return (applied, skipped.Values.ToList());
    }

    // ══════════════════════════════════════════
    //  Housekeeping
    // ══════════════════════════════════════════

    /// <summary>
    /// Removes restore staging and unfinished server-folder restores left behind by a crashed
    /// process. Called once on startup.
    /// </summary>
    public async Task WipeStagingAsync(CancellationToken ct)
    {
        if (Directory.Exists(ServerRestoreRoot))
        {
            foreach (var partial in Directory.EnumerateDirectories(ServerRestoreRoot, "*" + PartialSuffix))
                TryDeleteDirectory(partial);
        }
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var path in await db.ShareDefinitions.AsNoTracking().Select(s => s.Path).ToListAsync(ct))
        {
            if (!string.IsNullOrWhiteSpace(path))
                TryDeleteDirectory(Path.Combine(Path.TrimEndingDirectorySeparator(path), StagingFolderName));
        }
    }

    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
