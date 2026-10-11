using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
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
public sealed class BackupJobExecutor(
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

            var result = await RunBackupAsync(target, run, request, progress, ct);
            return new(share.Id, share.Name, result.SnapshotId, null, result.WarningCount, result.Warnings);
        }
        catch (ResticException ex)
        {
            logger.LogWarning("Backup of share {Share} in run {RunId} failed: {Code}.", share.Name, run.Id, ex.Code);
            if (AbortsRun(ex))
                throw;
            return new(share.Id, share.Name, null, ex.Code, 0, []);
        }
        finally
        {
            if (metaDir is not null)
                TryDeleteDirectory(metaDir);
        }
    }

    private async Task<ResticBackupResult> RunBackupAsync(
        ResticTarget target, BackupRun run, ResticBackupRequest request, Action<double> progress, CancellationToken ct)
    {
        var result = await restic.BackupAsync(target, request, p => progress(Math.Clamp(p.PercentDone, 0, 1)), ct);
        run.FilesNew += result.FilesNew;
        run.FilesChanged += result.FilesChanged;
        run.FilesUnmodified += result.FilesUnmodified;
        run.BytesAdded += result.BytesAdded;
        run.BytesProcessed += result.BytesProcessed;
        if (result.HasWarnings && !string.IsNullOrWhiteSpace(result.StderrTail))
            run.LogExcerpt = Tail((run.LogExcerpt + "\n" + result.StderrTail).Trim());
        return result;
    }

    /// <summary>Repository-level failures abort the whole run; source-level ones only that source.</summary>
    private static bool AbortsRun(ResticException ex)
        => ex.Code is "repo_unreachable" or "repo_not_found" or "wrong_password" or "lock_failed"
            or "access_denied" or "tls_error" or "restic_unavailable" or "insufficient_space";

    /// <summary>
    /// A whole pool (Global-only): every share folder in it plus folders that are no share, and
    /// one ACL manifest per share of the pool. Kaimo-internal entries are excluded like for a
    /// share; the version store only when the job asks for it.
    /// </summary>
    private async Task<BackupSourceOutcome> BackupPoolAsync(
        ApplicationDbContext db, ResticTarget target, BackupRepository repository, BackupJob job, BackupRun run,
        string pool, Action<double> progress, CancellationToken ct)
    {
        var name = PoolTag(pool);
        string? metaDir = null;
        try
        {
            var shares = (await db.ShareDefinitions.AsNoTracking().ToListAsync(ct))
                .Where(s => IsInside(s.Path, pool)).ToList();
            // Every pool backup point holds one manifest file per share besides the pool's files.
            GuardSource(pool, await db.BackupSnapshots.AnyAsync(
                s => s.RepositoryId == repository.Id && s.PoolPath == name && s.FileCount > shares.Count, ct));

            metaDir = Path.Combine(MetaRoot, "pool-" + PathHash(pool));
            CreatePrivateDirectory(metaDir);
            progress(0);
            var now = time.GetUtcNow().UtcDateTime;
            foreach (var share in shares)
                await BackupAclManifest.WriteAsync(db, share, Path.Combine(metaDir, share.Id.ToString("N") + "-" + BackupAclManifest.FileName), now, ct);

            var request = new ResticBackupRequest(
                [Path.TrimEndingDirectorySeparator(pool), metaDir],
                [
                    "kaimo", "job:" + job.Id.ToString("N"), "run:" + run.Id.ToString("N"),
                    "level:" + LevelTag(job.ContentLevel), "pool:" + name,
                ],
                BuildPoolExcludes(pool, shares, job.IncludeRecycleBin, IncludesVersions(job)));
            var result = await RunBackupAsync(target, run, request, progress, ct);
            return new(null, name, result.SnapshotId, null, result.WarningCount, result.Warnings);
        }
        catch (ResticException ex)
        {
            logger.LogWarning("Backup of pool {Pool} in run {RunId} failed: {Code}.", name, run.Id, ex.Code);
            if (AbortsRun(ex))
                throw;
            return new(null, name, null, ex.Code, 0, []);
        }
        finally
        {
            if (metaDir is not null)
                TryDeleteDirectory(metaDir);
        }
    }

    /// <summary>
    /// The "system" backup point: database dump and run info, for Full also the application
    /// data (DataProtection keys, certificates) and an external DataProtection certificate.
    /// </summary>
    private async Task<BackupSourceOutcome> BackupSystemAsync(
        ApplicationDbContext db, ResticTarget target, BackupJob job, BackupRun run, string systemDir,
        Action<double> progress, CancellationToken ct)
    {
        try
        {
            var paths = new List<string> { systemDir };
            var excludes = new List<string>();
            if (job.ContentLevel == BackupContentLevel.Full)
            {
                var applicationData = Path.TrimEndingDirectorySeparator(resolver.ApplicationDataPath);
                if (!Directory.Exists(applicationData))
                    throw new ResticException("source_unavailable", "The application data folder does not exist.");
                paths.Add(applicationData);
                // Caches and temp files (restic cache, SMB snapshot cache, ...) are no recovery data.
                excludes.Add(applicationData.Replace('\\', '/') + "/" + ShareEntryPolicy.InternalNamespacePrefix + "*");
                excludes.Add(ResticClient.TrimDirectory(restic.CacheDirectory));
                excludes.Add(ResticClient.TrimDirectory(resolver.TempRoot));
                var certificate = configuration["DataProtection:CertificatePath"];
                if (!string.IsNullOrWhiteSpace(certificate) && File.Exists(certificate) && !IsInside(certificate, applicationData))
                    paths.Add(certificate);
            }

            await WriteBackupInfoAsync(db, job, run, Path.Combine(systemDir, BackupInfoFileName), ct);
            var request = new ResticBackupRequest(
                paths,
                [
                    "kaimo", "job:" + job.Id.ToString("N"), "run:" + run.Id.ToString("N"),
                    "level:" + LevelTag(job.ContentLevel), "kind:system",
                ],
                excludes);
            var result = await RunBackupAsync(target, run, request, progress, ct);
            return new(null, SystemSourceKey, result.SnapshotId, null, result.WarningCount, result.Warnings);
        }
        catch (ResticException ex)
        {
            logger.LogWarning("System backup in run {RunId} failed: {Code}.", run.Id, ex.Code);
            if (AbortsRun(ex))
                throw;
            return new(null, SystemSourceKey, null, ex.Code, 0, []);
        }
    }

    /// <summary>What a disaster recovery needs to know besides the dump: where things lived.</summary>
    private async Task WriteBackupInfoAsync(ApplicationDbContext db, BackupJob job, BackupRun run, string path, CancellationToken ct)
    {
        var shares = await db.ShareDefinitions.AsNoTracking().OrderBy(s => s.Name)
            .Select(s => new { id = s.Id, name = s.Name, path = s.Path, isUserHomes = s.IsUserHomes }).ToListAsync(ct);
        var dump = new FileInfo(Path.Combine(Path.GetDirectoryName(path)!, DatabaseDumpFileName));
        var info = new
        {
            formatVersion = 1,
            createdAtUtc = time.GetUtcNow().UtcDateTime,
            kaimoVersion = typeof(BackupJobExecutor).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            jobId = job.Id,
            jobName = job.Name,
            runId = run.Id,
            contentLevel = LevelTag(job.ContentLevel),
            database = new { file = DatabaseDumpFileName, format = "pg_dump custom", sizeBytes = dump.Exists ? dump.Length : 0 },
            applicationDataPath = resolver.ApplicationDataPath,
            dataProtectionCertificatePath = configuration["DataProtection:CertificatePath"],
            pools = resolver.Pools,
            shares,
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }), ct);
    }

    /// <summary>
    /// Stored as the name of the system source in run outcomes; the UI shows it in its own
    /// language (runs execute without one).
    /// </summary>
    public const string SystemSourceKey = "kaimo:system";

    /// <summary>Display name of the system source in <paramref name="culture"/> (default: the thread's UI culture).</summary>
    public static string SystemSourceName(System.Globalization.CultureInfo? culture = null)
        => Resources.ResourceManager.GetString("Web_Backup_Source_System", culture) ?? "Database & system";

    /// <summary>Display name of a pool source ("Pool pool01").</summary>
    public static string PoolSourceName(string poolName, System.Globalization.CultureInfo? culture = null)
        => string.Format(culture ?? System.Globalization.CultureInfo.CurrentCulture,
            Resources.ResourceManager.GetString("Web_Backup_Source_Pool", culture) ?? "Pool {0}", poolName);

    /// <summary>The pool's folder name, usable as a restic tag value (no comma: restic splits tag lists on it).</summary>
    public static string PoolTag(string pool)
        => Path.GetFileName(Path.TrimEndingDirectorySeparator(pool)).Replace(',', '_');

    /// <summary>Stable per pool path, so the manifest staging path (part of the snapshot paths) never changes.</summary>
    private static string PathHash(string path)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(path))))[..16].ToLowerInvariant();

    public static bool IsInside(string path, string folder)
    {
        var p = Path.TrimEndingDirectorySeparator(path).Replace('\\', '/');
        var f = Path.TrimEndingDirectorySeparator(folder).Replace('\\', '/');
        return p.StartsWith(f + "/", StringComparison.Ordinal);
    }

    /// <summary>Version blobs are useless without their database rows, so they need a database level.</summary>
    public static bool IncludesVersions(BackupJob job)
        => job.IncludeVersions && job.ContentLevel != BackupContentLevel.Files;

    /// <summary>
    /// Excludes of a pool: its own internal entries (<c>.kaimo-*</c>: version store, space
    /// reservations, ...), the internal folders of every folder in it and the excludes of each
    /// of its shares (recycle bins, home folders). The version store is re-included last
    /// (restic negated pattern) when wanted.
    /// </summary>
    internal static List<string> BuildPoolExcludes(string pool, IEnumerable<ShareDefinition> shares, bool includeRecycleBin, bool includeVersions)
    {
        var root = Path.TrimEndingDirectorySeparator(pool).Replace('\\', '/');
        var excludes = new List<string>
        {
            root + "/" + ShareEntryPolicy.InternalNamespacePrefix + "*",
            root + "/*/" + ShareEntryPolicy.InternalNamespacePrefix + "*",
        };
        foreach (var share in shares)
            excludes.AddRange(BuildShareExcludes(share, includeRecycleBin).Where(e => !excludes.Contains(e)));
        if (includeVersions)
            excludes.Add("!" + root + "/" + ResticClient.EscapePattern(Services.PoolVersionStorageLocator.FolderName));
        return excludes;
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
    /// Refuses to back up a missing source or a source that suddenly holds no file although
    /// earlier snapshots had content — the classic sign of an unmounted pool, which would
    /// otherwise silently produce an "everything deleted" snapshot that retention then uses to
    /// push out the good ones.
    /// </summary>
    internal static void GuardSource(string path, bool hadContentBefore)
    {
        if (!Directory.Exists(path))
            throw new ResticException("source_unavailable", "The share folder does not exist (pool not mounted?).");
        if (hadContentBefore && !HasUserFile(path))
            throw new ResticException("source_empty", "The source holds no file although earlier backups had content (pool not mounted?).");
    }

    /// <summary>
    /// True when a file exists anywhere below <paramref name="root"/> outside Kaimo-internal
    /// folders. Folders do not count: on an unmounted pool Kaimo itself recreates share folders,
    /// their <c>.kaimo-*</c> folders and the home folders, but never files. Stops at the first file.
    /// </summary>
    internal static bool HasUserFile(string root)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(root)]);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
            {
                if (entry.Name.StartsWith(ShareEntryPolicy.InternalNamespacePrefix, StringComparison.Ordinal))
                    continue;
                if (entry is FileInfo)
                    return true;
                if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    pending.Push((DirectoryInfo)entry);
            }
        }
        return false;
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
