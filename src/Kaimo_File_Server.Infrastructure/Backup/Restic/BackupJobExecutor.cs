using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>Per-source outcome of a backup run, stored in <see cref="BackupRun.DetailsJson"/>.</summary>
public sealed record BackupSourceOutcome(
    Guid? ShareId,
    string Name,
    string? SnapshotId,
    string? ErrorCode,
    int WarningCount,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Executes one queued backup run: one restic snapshot per source (so scoping, retention
/// groups and parent detection stay per share, and one failing share does not fail the
/// others), each carrying the share's ACL manifest. Afterwards the snapshot cache is
/// reconciled and failures/warnings are published as notifications.
/// </summary>
public sealed class BackupJobExecutor(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ResticTargetResolver resolver,
    ResticClient restic,
    IConfiguration configuration,
    TimeProvider time,
    INotificationPublisher notifications,
    ILogger<BackupJobExecutor> logger)
{
    private const int MaximumLogExcerpt = 8 * 1024;

    /// <summary>Staging folder for ACL manifests; the per-share sub path must stay stable across runs.</summary>
    public string MetaRoot => configuration["Backup:Restic:MetaDirectory"]
        ?? Path.Combine(configuration["Backup:RootPath"] ?? "/data/kaimo-backups", "restic-meta");

    public async Task ExecuteAsync(Guid runId, Action<string?, int> report, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.BackupRuns.FirstAsync(r => r.Id == runId, ct);
        var job = await db.BackupJobs.Include(j => j.Sources).FirstOrDefaultAsync(j => j.Id == run.JobId, ct);
        var repository = await db.BackupRepositories.FirstAsync(r => r.Id == run.RepositoryId, ct);

        run.Status = BackupRunStatus.Running;
        run.StartedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(CancellationToken.None);

        var outcomes = new List<BackupSourceOutcome>();
        string? stderrTail = null;
        try
        {
            if (job is null)
                throw new ResticException("job_missing", "The backup job no longer exists.");
            if (repository.State != BackupRepositoryState.Active)
                throw new ResticException("repository_not_active", "The repository is not active (recovery kit not confirmed or disabled).");

            using var target = await resolver.ResolveAsync(repository, ct);
            EnsureLocalFreeSpace(repository);
            await restic.UnlockAsync(target, ct);

            var sources = job.Sources.Where(s => s.Kind == BackupSourceKind.Share && s.ShareId is not null).ToList();
            var shareIds = sources.Select(s => s.ShareId!.Value).ToList();
            var shares = await db.ShareDefinitions.AsNoTracking().Where(s => shareIds.Contains(s.Id)).ToListAsync(ct);

            for (var i = 0; i < sources.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var share = shares.FirstOrDefault(s => s.Id == sources[i].ShareId);
                var index = i;
                outcomes.Add(await BackupShareAsync(db, target, repository, job, run, share, sources[i].ShareId!.Value,
                    pct => report(share?.Name, (int)((index + pct) * 100 / sources.Count)), ct));
            }

            report(null, 100);
            await RefreshSnapshotCacheAsync(db, repository.Id, target, ct);
            repository.LastReachableAtUtc = time.GetUtcNow().UtcDateTime;
            repository.LastErrorCode = null;
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
            stderrTail = ex.Data["stderr"] as string;
            if (ex is ResticException { Code: "repo_unreachable" or "repo_not_found" or "wrong_password" or "access_denied" or "tls_error" })
                repository.LastErrorCode = run.ErrorCode;
            if (ex is not ResticException)
                logger.LogError(ex, "Backup run {RunId} failed unexpectedly.", runId);
        }

        if (run.Status == BackupRunStatus.Running)
        {
            var failed = outcomes.Where(o => o.ErrorCode is not null).ToList();
            run.Status = failed.Count > 0
                ? BackupRunStatus.Failed
                : outcomes.Any(o => o.WarningCount > 0) ? BackupRunStatus.SucceededWithWarnings : BackupRunStatus.Succeeded;
            run.ErrorCode = failed.Count == 0 ? null : failed.Count == 1 ? failed[0].ErrorCode : "sources_failed";
        }

        run.FinishedAtUtc = time.GetUtcNow().UtcDateTime;
        run.WarningCount = outcomes.Sum(o => o.WarningCount);
        run.SnapshotIdsJson = JsonSerializer.Serialize(outcomes.Where(o => o.SnapshotId is not null).Select(o => o.SnapshotId));
        run.DetailsJson = JsonSerializer.Serialize(new { sources = outcomes });
        run.LogExcerpt = Tail(stderrTail);

        if (job is not null)
        {
            job.LastRunAtUtc = run.FinishedAtUtc;
            job.LastStatus = run.Status;
            if (run.Status is BackupRunStatus.Succeeded or BackupRunStatus.SucceededWithWarnings)
                job.LastSuccessAtUtc = run.FinishedAtUtc;
        }
        await db.SaveChangesAsync(CancellationToken.None);

        if (job is not null)
            await PublishAsync(run, job, repository, outcomes);
    }

    private async Task<BackupSourceOutcome> BackupShareAsync(
        ApplicationDbContext db, ResticTarget target, BackupRepository repository, BackupJob job, BackupRun run,
        ShareDefinition? share, Guid shareId, Action<double> progress, CancellationToken ct)
    {
        if (share is null)
            return new(shareId, shareId.ToString(), null, "share_missing", 0, []);

        string? metaDir = null;
        try
        {
            GuardSource(share.Path, await HadContentBeforeAsync(db, repository.Id, share.Id, ct));

            metaDir = Path.Combine(MetaRoot, "share-" + share.Id.ToString("N"));
            CreatePrivateDirectory(metaDir);
            var manifest = Path.Combine(metaDir, BackupAclManifest.FileName);
            progress(0);
            await BackupAclManifest.WriteAsync(db, share, manifest, time.GetUtcNow().UtcDateTime, ct);

            var request = new ResticBackupRequest(
                [Path.TrimEndingDirectorySeparator(share.Path), manifest],
                [
                    "kaimo", "job:" + job.Id.ToString("N"), "run:" + run.Id.ToString("N"),
                    "level:" + LevelTag(job.ContentLevel), "share:" + share.Id.ToString("N"),
                ],
                BuildShareExcludes(share, job.IncludeRecycleBin));

            var result = await restic.BackupAsync(target, request,
                p => progress(Math.Clamp(p.PercentDone, 0, 1)), ct);

            run.FilesNew += result.FilesNew;
            run.FilesChanged += result.FilesChanged;
            run.FilesUnmodified += result.FilesUnmodified;
            run.BytesAdded += result.BytesAdded;
            run.BytesProcessed += result.BytesProcessed;
            return new(share.Id, share.Name, result.SnapshotId, null, result.WarningCount, result.Warnings);
        }
        catch (ResticException ex)
        {
            logger.LogWarning("Backup of share {Share} in run {RunId} failed: {Code}.", share.Name, run.Id, ex.Code);
            // Repository-level failures abort the whole run; share-level ones only this source.
            if (ex.Code is "repo_unreachable" or "repo_not_found" or "wrong_password" or "lock_failed"
                or "access_denied" or "tls_error" or "restic_unavailable" or "insufficient_space")
                throw;
            return new(share.Id, share.Name, null, ex.Code, 0, []);
        }
        finally
        {
            if (metaDir is not null)
                TryDeleteDirectory(metaDir);
        }
    }

    /// <summary>
    /// Excludes Kaimo-internal folders (always) and recycle bins (unless included), anchored
    /// at the share root (for the homes share additionally at each home folder) so user
    /// folders deeper in the tree that merely share the name are still backed up.
    /// </summary>
    internal static List<string> BuildShareExcludes(ShareDefinition share, bool includeRecycleBin)
    {
        var root = Path.TrimEndingDirectorySeparator(share.Path).Replace('\\', '/');
        var bases = share.IsUserHomes ? new[] { root, root + "/*" } : [root];
        var excludes = new List<string>();
        foreach (var b in bases)
        {
            excludes.Add(b + "/" + ShareEntryPolicy.InternalNamespacePrefix + "*");
            if (!includeRecycleBin)
                excludes.Add(b + "/" + ShareEntryPolicy.RecycleBinName);
        }
        return excludes;
    }

    /// <summary>
    /// Refuses to back up a missing source or a source that suddenly became empty although
    /// earlier snapshots had content — the classic sign of an unmounted pool, which would
    /// otherwise silently produce an "everything deleted" snapshot.
    /// </summary>
    internal static void GuardSource(string path, bool hadContentBefore)
    {
        if (!Directory.Exists(path))
            throw new ResticException("source_unavailable", "The share folder does not exist (pool not mounted?).");
        if (hadContentBefore && !Directory.EnumerateFileSystemEntries(path).Any())
            throw new ResticException("source_empty", "The share folder is empty although earlier backups had content (pool not mounted?).");
    }

    private static Task<bool> HadContentBeforeAsync(ApplicationDbContext db, Guid repositoryId, Guid shareId, CancellationToken ct)
        => db.BackupSnapshots.AnyAsync(s => s.RepositoryId == repositoryId && s.ShareId == shareId && s.FileCount > 1, ct);

    private void EnsureLocalFreeSpace(BackupRepository repository)
    {
        if (repository.Backend != BackupBackend.Local || repository.MinFreeSpaceGb <= 0)
            return;
        var path = ResticRepositorySettings.Parse(repository.SettingsJson).Path;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            throw new ResticException("repo_not_found", "The local repository folder does not exist.");
        long free;
        try { free = new DriveInfo(path).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return; }
        if (free < repository.MinFreeSpaceGb * 1024L * 1024 * 1024)
            throw new ResticException("insufficient_space", "The repository target has less free space than configured.");
    }

    /// <summary>Re-reads the repository's snapshot list into the local cache.</summary>
    public async Task RefreshSnapshotCacheAsync(ApplicationDbContext db, Guid repositoryId, ResticTarget target, CancellationToken ct)
    {
        var snapshots = await restic.SnapshotsAsync(target, ct);
        var existing = await db.BackupSnapshots.Where(s => s.RepositoryId == repositoryId).ToListAsync(ct);
        var byId = existing.ToDictionary(s => s.SnapshotId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var info in snapshots)
        {
            seen.Add(info.Id);
            if (!byId.TryGetValue(info.Id, out var row))
            {
                row = new BackupSnapshot { RepositoryId = repositoryId, SnapshotId = info.Id };
                db.BackupSnapshots.Add(row);
            }
            ApplySnapshotInfo(row, info);
        }
        db.BackupSnapshots.RemoveRange(existing.Where(s => !seen.Contains(s.SnapshotId)));
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Derives kind, job, run, share and level from the snapshot tags.</summary>
    internal static void ApplySnapshotInfo(BackupSnapshot row, ResticSnapshotInfo info)
    {
        string? Tag(string prefix) => info.Tags.FirstOrDefault(t => t.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        Guid? TagGuid(string prefix) => Guid.TryParseExact(Tag(prefix) ?? string.Empty, "N", out var g) ? g : null;

        var isKaimo = info.Tags.Contains("kaimo");
        row.TimeUtc = info.TimeUtc;
        row.JobId = isKaimo ? TagGuid("job:") : null;
        row.RunId = isKaimo ? TagGuid("run:") : null;
        row.ShareId = isKaimo ? TagGuid("share:") : null;
        row.PoolPath = isKaimo ? Tag("pool:") : null;
        row.Kind = !isKaimo ? BackupSnapshotKind.Foreign
            : row.ShareId is not null ? BackupSnapshotKind.Share
            : row.PoolPath is not null ? BackupSnapshotKind.Pool
            : info.Tags.Contains("kind:system") ? BackupSnapshotKind.System
            : BackupSnapshotKind.Foreign;
        row.ContentLevel = Tag("level:") switch
        {
            "db" => BackupContentLevel.FilesAndDatabase,
            "full" => BackupContentLevel.Full,
            _ => BackupContentLevel.Files,
        };
        row.PathsJson = JsonSerializer.Serialize(info.Paths);
        row.TagsJson = JsonSerializer.Serialize(info.Tags);
        row.FileCount = info.FileCount;
        row.TotalBytes = info.TotalBytes;
    }

    private async Task PublishAsync(BackupRun run, BackupJob job, BackupRepository repository, List<BackupSourceOutcome> outcomes)
    {
        if (run.Status == BackupRunStatus.Failed)
        {
            var failed = outcomes.Where(o => o.ErrorCode is not null).Select(o => o.Name).ToList();
            await notifications.PublishAsync(NotificationEvents.FileBackupFailed(
                job.Id, job.Name, repository.Name, job.CreatedByUserId, run.ErrorCode ?? "restic_failed",
                failed.Count > 0 ? string.Join(", ", failed) : "-"));
        }
        else if (run.Status == BackupRunStatus.SucceededWithWarnings)
        {
            await notifications.PublishAsync(NotificationEvents.FileBackupWarnings(
                job.Id, job.Name, repository.Name, job.CreatedByUserId, run.WarningCount));
        }
    }

    internal static string LevelTag(BackupContentLevel level) => level switch
    {
        BackupContentLevel.FilesAndDatabase => "db",
        BackupContentLevel.Full => "full",
        _ => "files",
    };

    private static string? Tail(string? text)
        => string.IsNullOrEmpty(text) ? null : text.Length <= MaximumLogExcerpt ? text : text[^MaximumLogExcerpt..];

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

    /// <summary>Removes manifest staging left behind by a crashed process. Called once on startup.</summary>
    public void WipeMetaRoot() => TryDeleteDirectory(MetaRoot);
}
