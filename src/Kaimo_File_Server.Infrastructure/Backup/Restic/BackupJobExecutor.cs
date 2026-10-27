using System.Text.Json;
using Kaimo_File_Server.Core.Domain.Backup;
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
/// others), each carrying the ACL manifests of its shares. Database and Full jobs dump the
/// database first and finish with one "system" snapshot (dump, run info and, for Full, the
/// application data). Afterwards the snapshot cache is reconciled and failures/warnings are
/// published as notifications.
/// </summary>
public sealed partial class BackupJobExecutor(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ResticTargetResolver resolver,
    ResticClient restic,
    IConfiguration configuration,
    TimeProvider time,
    INotificationPublisher notifications,
    IDatabaseBackupService database,
    ILogger<BackupJobExecutor> logger)
{
    private const int MaximumLogExcerpt = 8 * 1024;

    /// <summary>File names inside the system snapshot (part of the documented recovery layout).</summary>
    public const string DatabaseDumpFileName = "kaimo-database.dump";
    public const string BackupInfoFileName = "kaimo-backup-info.json";

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
        var previousRepositoryError = repository.LastErrorCode;
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
            var pools = job.Sources.Where(s => s.Kind == BackupSourceKind.Pool && !string.IsNullOrWhiteSpace(s.PoolPath))
                .Select(s => s.PoolPath!).Distinct(StringComparer.Ordinal).ToList();
            var withSystem = job.ContentLevel != BackupContentLevel.Files;
            var shareIds = sources.Select(s => s.ShareId!.Value).ToList();
            var shares = await db.ShareDefinitions.AsNoTracking().Where(s => shareIds.Contains(s.Id)).ToListAsync(ct);
            var total = Math.Max(1, sources.Count + pools.Count + (withSystem ? 1 : 0));
            var done = 0;
            Action<double> Progress(string? name) => pct => report(name, (int)((done + pct) * 100 / total));

            // The database is dumped before any file is read, so the files of this run are not
            // older than the database (a version cleanup between dump and pool backup can still
            // remove blobs the dump references).
            string? systemDir = null;
            string? dumpError = null;
            try
            {
                if (withSystem)
                {
                    systemDir = Path.Combine(MetaRoot, "system");
                    CreatePrivateDirectory(systemDir);
                    report(SystemSourceName(), 0);
                    try
                    {
                        await database.DumpToAsync(Path.Combine(systemDir, DatabaseDumpFileName), ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        dumpError = "database_dump_failed";
                        // pg_dump's stderr; restic warnings of the file sources are appended after it.
                        run.LogExcerpt = Tail(ex.Message);
                        logger.LogWarning(ex, "Database dump of backup run {RunId} failed.", runId);
                    }
                }

                foreach (var source in sources)
                {
                    ct.ThrowIfCancellationRequested();
                    var share = shares.FirstOrDefault(s => s.Id == source.ShareId);
                    outcomes.Add(await BackupShareAsync(db, target, repository, job, run, share, source.ShareId!.Value, Progress(share?.Name), ct));
                    done++;
                }

                foreach (var pool in pools)
                {
                    ct.ThrowIfCancellationRequested();
                    outcomes.Add(await BackupPoolAsync(db, target, repository, job, run, pool, Progress(PoolTag(pool)), ct));
                    done++;
                }

                if (withSystem)
                {
                    ct.ThrowIfCancellationRequested();
                    outcomes.Add(dumpError is not null
                        ? new(null, SystemSourceKey, null, dumpError, 0, [])
                        : await BackupSystemAsync(db, target, job, run, systemDir!, Progress(SystemSourceName()), ct));
                }
            }
            finally
            {
                if (systemDir is not null)
                    TryDeleteDirectory(systemDir);
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
            if (BackupMaintenanceExecutor.IsRepositoryError(run.ErrorCode))
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
        if (stderrTail is not null)
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
        if (previousRepositoryError is null && repository.LastErrorCode is { } repositoryError)
            await notifications.PublishAsync(NotificationEvents.FileBackupRepositoryUnreachable(repository.Id, repository.Name, repositoryError));

        if (job is not null && run.Status is BackupRunStatus.Succeeded or BackupRunStatus.SucceededWithWarnings
            && !job.Retention.IsEmpty && !repository.IsAppendOnly)
        {
            report(BackupUiText.RunType(BackupRunType.Forget), 100);
            await ApplyRetentionAsync(db, repository, job, run.Trigger, ct);
        }
    }

    /// <summary>True when the repository holds system backup points of the job (they need their own retention pass).</summary>
    internal static Task<bool> HasSystemPointsAsync(ApplicationDbContext db, Guid repositoryId, Guid jobId, CancellationToken ct)
        => db.BackupSnapshots.AnyAsync(s => s.RepositoryId == repositoryId && s.JobId == jobId && s.Kind == BackupSnapshotKind.System, ct);

    /// <summary>
    /// Applies the job's retention right after a successful backup, as its own run. Only the
    /// backup points are removed here; their data is freed by the next repository maintenance
    /// (prune), which needs an exclusive lock and is too expensive for every run.
    /// </summary>
    private async Task ApplyRetentionAsync(ApplicationDbContext db, BackupRepository repository, BackupJob job, BackupRunTrigger trigger, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var run = new BackupRun
        {
            RepositoryId = repository.Id,
            JobId = job.Id,
            Type = BackupRunType.Forget,
            Trigger = trigger,
            Status = BackupRunStatus.Running,
            QueuedAtUtc = now,
            StartedAtUtc = now,
        };
        db.BackupRuns.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);

        var details = new BackupMaintenanceDetails { JobsApplied = 1 };
        try
        {
            using var target = await resolver.ResolveAsync(repository, ct);
            details.SnapshotsRemoved = await restic.ForgetAsync(target, job.Id, job.Retention, ct,
                await HasSystemPointsAsync(db, repository.Id, job.Id, ct));
            if (details.SnapshotsRemoved > 0)
                await RefreshSnapshotCacheAsync(db, repository.Id, target, ct);
            run.Status = BackupRunStatus.Succeeded;
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
            run.LogExcerpt = Tail(ex.Data["stderr"] as string);
            if (ex is not ResticException)
                logger.LogError(ex, "Retention of backup job {Job} failed unexpectedly.", job.Name);
            await notifications.PublishAsync(NotificationEvents.FileBackupMaintenanceFailed(
                repository.Id, repository.Name, BackupUiText.RunType(BackupRunType.Forget), run.ErrorCode));
        }

        run.FinishedAtUtc = time.GetUtcNow().UtcDateTime;
        run.DetailsJson = details.Serialize();
        await db.SaveChangesAsync(CancellationToken.None);
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
            var failed = outcomes.Where(o => o.ErrorCode is not null)
                .Select(o => o.Name == SystemSourceKey ? SystemSourceName() : o.Name).ToList();
            await notifications.PublishAsync(NotificationEvents.FileBackupFailed(
                job.Id, job.Name, repository.Name, job.CreatedByUserId, run.ErrorCode ?? "restic_failed",
                failed.Count > 0 ? string.Join(", ", failed) : "-"));
        }
        else if (run.Status == BackupRunStatus.SucceededWithWarnings)
        {
            await notifications.PublishAsync(NotificationEvents.FileBackupWarnings(
                job.Id, job.Name, repository.Name, job.CreatedByUserId, run.WarningCount));
        }
        else if (run.Status == BackupRunStatus.Succeeded)
        {
            // Both timestamps are set by ExecuteAsync before a run reaches this point.
            await notifications.PublishAsync(NotificationEvents.FileBackupSucceeded(
                job.Id, job.Name, repository.Name, job.CreatedByUserId, run.Trigger.ToString(),
                run.FilesNew, run.FilesChanged, run.BytesAdded, run.FinishedAtUtc!.Value - run.StartedAtUtc!.Value));
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
