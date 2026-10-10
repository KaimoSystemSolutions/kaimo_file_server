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
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>Where restored entries go. Values are persisted in run details; never renumber.</summary>
public enum BackupRestoreMode
{
    /// <summary>Into a new "Restored …" folder next to the original location.</summary>
    NewFolder = 0,

    /// <summary>Back to the original location; existing files are handled by the conflict policy.</summary>
    InPlace = 1,

    /// <summary>Into a new "Restored …" folder in another share.</summary>
    OtherShare = 2,

    /// <summary>Streamed to the browser (file raw, folder as ZIP); only recorded, never queued.</summary>
    Download = 3,
}

/// <summary>What happens when an in-place restore meets an existing entry. Never deletes anything.</summary>
public enum BackupRestoreConflict
{
    /// <summary>Replace the file; its current content is kept as a file version first.</summary>
    Overwrite = 0,
    Skip = 1,

    /// <summary>Restore beside it as "name (restored yyyy-MM-dd).ext".</summary>
    Rename = 2,
}

/// <summary>
/// A restore as requested in the UI: entries of <see cref="FolderPath"/> (share-relative, as
/// recorded in the backup) — the named <see cref="Items"/>, or all entries when empty.
/// </summary>
public sealed record BackupRestoreRequest(
    Guid RepositoryId,
    string SnapshotId,
    Guid ShareId,
    string FolderPath,
    IReadOnlyList<string> Items,
    BackupRestoreMode Mode,
    BackupRestoreConflict Conflict,
    bool RestorePermissions,
    Guid? TargetShareId = null,
    string? TargetFolder = null,
    string? Culture = null)
{
    /// <summary>UI language of the requesting admin: names the "Restored …" folder and rename suffix.</summary>
    // A method, not a property: the request is serialized into the run details.
    public CultureInfo GetUiCulture()
    {
        try { return string.IsNullOrEmpty(Culture) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(Culture); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }
}

/// <summary>A principal of the backed-up permissions that no longer exists.</summary>
public sealed record BackupSkippedPrincipal(Guid Id, string? Name);

/// <summary>Result of a restore, stored in <see cref="BackupRun.DetailsJson"/> and shown in the activity log.</summary>
public sealed class BackupRestoreOutcome
{
    public string? SourceShareName { get; set; }
    public Guid DestinationShareId { get; set; }
    public string? DestinationShareName { get; set; }
    public string DestinationPath { get; set; } = string.Empty;
    public long FilesRestored { get; set; }
    public long BytesRestored { get; set; }
    public int Overwritten { get; set; }

    /// <summary>Existing files with exactly the backed-up content; left alone.</summary>
    public int Unchanged { get; set; }
    public int VersionsCreated { get; set; }
    public int Skipped { get; set; }
    public int Renamed { get; set; }
    public int AclEntriesApplied { get; set; }
    public List<BackupSkippedPrincipal> SkippedPrincipals { get; set; } = [];

    /// <summary>Selected entries that were missing in the backup or point into a reserved folder.</summary>
    public List<string> SkippedItems { get; set; } = [];
}

/// <summary>Content of <see cref="BackupRun.DetailsJson"/> for restore runs.</summary>
public sealed record BackupRestoreDetails(BackupRestoreRequest Request, BackupRestoreOutcome? Result = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static BackupRestoreDetails? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var details = JsonSerializer.Deserialize<BackupRestoreDetails>(json, Json);
            return details?.Request is null ? null : details;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Executes one queued restore run. restic restores into a private staging folder inside the
/// destination share (<c>.kaimo-restore</c>, same file system ⇒ atomic moves, invisible to
/// users and excluded from backups); the staged entries are then moved into place under the
/// share lock. In-place restores never delete anything and keep a file version of every file
/// they overwrite. Optionally the backed-up owners and ACLs are written back.
/// </summary>
public sealed class BackupRestoreExecutor(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ResticTargetResolver resolver,
    ResticClient restic,
    IFileVersionService versions,
    IFileChangeLog changeLog,
    ShareLockManager locks,
    TimeProvider time,
    ILogger<BackupRestoreExecutor> logger)
{
    public const string StagingFolderName = ShareEntryPolicy.InternalNamespacePrefix + "restore";
    private const double RequiredSpaceFactor = 1.1;

    public async Task ExecuteAsync(Guid runId, Action<string?, int> report, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.BackupRuns.FirstAsync(r => r.Id == runId, ct);
        var details = BackupRestoreDetails.TryParse(run.DetailsJson);

        run.Status = BackupRunStatus.Running;
        run.StartedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(CancellationToken.None);

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

            if (request.RestorePermissions)
            {
                report(null, 95);
                var manifestPath = ManifestPath(snapshot)
                                   ?? throw new ResticException("manifest_missing", "The backup point holds no permission manifest.");
                var manifestFile = Path.Combine(staging, BackupAclManifest.FileName);
                await using (var file = new FileStream(manifestFile, FileMode.Create, FileAccess.Write))
                    await restic.DumpAsync(target, request.SnapshotId, manifestPath, asZipArchive: false, file, ct);
                await using var manifest = File.OpenRead(manifestFile);
                // Own context: ApplyAclAsync clears its change tracker in batches, which would
                // otherwise detach this run's row and lose its final status.
                await using var aclDb = await dbFactory.CreateDbContextAsync(ct);
                var (applied, skipped) = await ApplyAclAsync(aclDb, manifest, destination.Id, destinationRoot,
                    p => MapPath(p, folder, items, destinationBase, merge.Untouched), run.ActorUserId ?? Guid.Empty, now, ct);
                outcome.AclEntriesApplied = applied;
                outcome.SkippedPrincipals = skipped;
            }

            run.Status = outcome.SkippedItems.Count > 0 || outcome.SkippedPrincipals.Count > 0
                ? BackupRunStatus.SucceededWithWarnings
                : BackupRunStatus.Succeeded;
            run.WarningCount = outcome.SkippedItems.Count + outcome.SkippedPrincipals.Count;
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
    /// when it is not part of the selection or was left untouched (skipped / kept by a rename).
    /// </summary>
    internal static string? MapPath(
        string manifestPath, string folder, IReadOnlySet<string> items, string destinationBase, IReadOnlyCollection<string> untouched)
    {
        var rest = folder.Length == 0 ? manifestPath : RelativeTo(manifestPath, folder);
        if (!IsSelected(rest, items))
            return null;
        var destination = ShareRelativePath.Combine(destinationBase, rest);
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

    private static void EnsureFreeSpace(string path, long required)
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

    private sealed record ManifestAce(Guid PrincipalId, string? PrincipalName, int EntryType, long Permissions, int Inheritance);
    private sealed record ManifestEntry(string Path, bool IsDirectory, Guid OwnerId, string? OwnerName, List<ManifestAce>? Acl);
    private sealed record ManifestDocument(int FormatVersion, List<ManifestEntry>? Entries);

    /// <summary>
    /// Writes owners and ACL entries of the manifest back onto the restored paths of
    /// <paramref name="shareId"/> (<paramref name="map"/> returns null for paths that are not
    /// restored). Principals that no longer exist are skipped and returned; an unknown owner
    /// keeps the current owner (new rows: the restoring admin).
    /// </summary>
    internal static async Task<(int Applied, List<BackupSkippedPrincipal> Skipped)> ApplyAclAsync(
        ApplicationDbContext db, Stream manifest, Guid shareId, string shareRoot, Func<string, string?> map,
        Guid actorUserId, DateTime nowUtc, CancellationToken ct)
    {
        // ponytail: whole manifest in memory; switch to a streaming reader for shares with millions of ACL rows.
        var document = await JsonSerializer.DeserializeAsync<ManifestDocument>(manifest, BackupRestoreDetails.Json, ct)
                       ?? throw new ResticException("manifest_invalid", "The permission manifest could not be read.");
        if (document.FormatVersion > BackupAclManifest.FormatVersion)
            throw new ResticException("manifest_invalid", "The permission manifest was written by a newer version.");

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

    /// <summary>Removes restore staging left behind by a crashed process. Called once on startup.</summary>
    public async Task WipeStagingAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var path in await db.ShareDefinitions.AsNoTracking().Select(s => s.Path).ToListAsync(ct))
        {
            if (!string.IsNullOrWhiteSpace(path))
                TryDeleteDirectory(Path.Combine(Path.TrimEndingDirectorySeparator(path), StagingFolderName));
        }
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Moves staged entries into a share. Missing entries are moved as a whole (a directory in
/// one atomic rename); existing directories are merged recursively; existing files follow the
/// conflict policy. Nothing in the destination is ever deleted.
/// </summary>
internal sealed class RestoreMerge(
    string shareRoot,
    Guid shareId,
    BackupRestoreConflict conflict,
    IFileVersionService versions,
    string? userId,
    string renameSuffix,
    BackupRestoreOutcome outcome)
{
    /// <summary>Destination paths that kept their current content (skipped or restored under a new name).</summary>
    public List<string> Untouched { get; } = [];

    public async Task MoveAsync(string source, string relativeDestination)
    {
        var destination = Absolute(relativeDestination);
        var sourceIsDirectory = Directory.Exists(source);

        if (!Path.Exists(destination))
        {
            if (sourceIsDirectory) Directory.Move(source, destination);
            else File.Move(source, destination);
            return;
        }

        if (sourceIsDirectory && Directory.Exists(destination))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(source).ToList())
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith(ShareEntryPolicy.InternalNamespacePrefix, StringComparison.Ordinal))
                    continue;
                await MoveAsync(child, ShareRelativePath.Combine(relativeDestination, name));
            }
            return;
        }

        // Same content as in the backup: nothing to restore, no version, no renamed copy.
        if (!sourceIsDirectory && File.Exists(destination) && await SameContentAsync(source, destination))
        {
            outcome.Unchanged++;
            return;
        }

        if (conflict == BackupRestoreConflict.Skip)
        {
            outcome.Skipped++;
            Untouched.Add(relativeDestination);
            return;
        }

        // A folder cannot replace a file (and vice versa) without deleting it: restore beside it.
        if (conflict == BackupRestoreConflict.Rename || sourceIsDirectory || Directory.Exists(destination))
        {
            var renamed = Absolute(RenamedPath(relativeDestination, !sourceIsDirectory));
            if (sourceIsDirectory) Directory.Move(source, renamed);
            else File.Move(source, renamed);
            outcome.Renamed++;
            Untouched.Add(relativeDestination);
            return;
        }

        await using (var current = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (await versions.CreateVersionAsync(shareId, relativeDestination, current, userId) is not null)
                outcome.VersionsCreated++;
        }
        File.Move(source, destination, overwrite: true);
        outcome.Overwritten++;
    }

    private static async Task<bool> SameContentAsync(string a, string b)
    {
        await using var first = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var second = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (first.Length != second.Length)
            return false;
        var x = new byte[81920];
        var y = new byte[81920];
        int read;
        while ((read = await first.ReadAsync(x)) > 0)
        {
            await second.ReadExactlyAsync(y.AsMemory(0, read));
            if (!x.AsSpan(0, read).SequenceEqual(y.AsSpan(0, read)))
                return false;
        }
        return true;
    }

    /// <summary>"report (restored 2026-10-10).pdf", numbered if that exists too.</summary>
    private string RenamedPath(string relative, bool keepExtension)
    {
        var parent = ShareRelativePath.GetParent(relative);
        var name = ShareRelativePath.GetFileName(relative);
        var extension = keepExtension ? Path.GetExtension(name) : string.Empty;
        var stem = name[..^extension.Length];
        var candidate = ShareRelativePath.Combine(parent, stem + renameSuffix + extension);
        for (var n = 2; Path.Exists(Absolute(candidate)); n++)
            candidate = ShareRelativePath.Combine(parent, $"{stem}{renameSuffix} ({n}){extension}");
        return candidate;
    }

    private string Absolute(string relative)
        => ShareRelativePath.ToContainedAbsolutePath(shareRoot, relative, allowInternalNamespace: false);
}
