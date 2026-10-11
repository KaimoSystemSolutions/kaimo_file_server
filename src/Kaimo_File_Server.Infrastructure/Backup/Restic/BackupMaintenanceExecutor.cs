using System.Security.Cryptography;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>Outcome of one maintenance step, stored in <see cref="BackupRun.DetailsJson"/>; unused fields stay null.</summary>
public sealed class BackupMaintenanceDetails
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Forget: backup points removed / jobs whose retention was applied.</summary>
    public int? SnapshotsRemoved { get; set; }
    public int? JobsApplied { get; set; }

    /// <summary>Check: the data subset read ("3/12"), and the errors restic reported.</summary>
    public string? ReadDataSubset { get; set; }
    public List<string>? CheckErrors { get; set; }

    /// <summary>Test restore: files compared, files skipped because they changed since the backup, and differences.</summary>
    public int? FilesVerified { get; set; }
    public int? FilesChanged { get; set; }
    public List<string>? Mismatches { get; set; }

    /// <summary>Test restore: the newest database dump was read back and pg_restore could parse it (null: no dump).</summary>
    public bool? DatabaseDumpOk { get; set; }

    /// <summary>Copy: the mirror repository and how many backup points it received.</summary>
    public string? MirrorName { get; set; }
    public int? SnapshotsCopied { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static BackupMaintenanceDetails? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try { return JsonSerializer.Deserialize<BackupMaintenanceDetails>(json, Json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Repository maintenance, run on the backup lane (never in parallel with a backup): stale-lock
/// unlock → retention (<c>forget</c> per job, also for jobs whose points arrive via a mirror;
/// backups apply their own retention right away) → <c>prune</c> (frees the data of every removed
/// point) → <c>check</c> (monthly with a data subset) → test restore of a random sample
/// compared with the live files → <c>copy</c> to the mirror + mirror check. Each step is its own
/// <see cref="BackupRun"/>. Append-only repositories skip forget and prune; a failed check
/// skips the test restore and the copy, so damage is never propagated to the mirror.
/// </summary>
public sealed class BackupMaintenanceExecutor(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ResticTargetResolver resolver,
    ResticClient restic,
    BackupJobExecutor jobs,
    TimeProvider time,
    INotificationPublisher notifications,
    IDatabaseBackupService database,
    ILogger<BackupMaintenanceExecutor> logger)
{
    /// <summary>Larger files are left out of the test-restore sample to keep maintenance cheap.</summary>
    public const long MaximumSampleFileBytes = 256L * 1024 * 1024;

    private const int MaximumLogExcerpt = 8 * 1024;
    private const int MaximumListedFiles = 50;

    // Nested types cannot see primary-constructor parameters.
    private ResticTargetResolver Resolver => resolver;
    private ResticClient Client => restic;
    private BackupJobExecutor Jobs => jobs;
    private TimeProvider Time => time;
    private INotificationPublisher Notifications => notifications;
    private IDatabaseBackupService Database => database;
    private ILogger Logger => logger;

    /// <summary>
    /// The repository itself plus every repository that mirrors into it directly or through
    /// other mirrors (<paramref name="mirrorTargets"/>: repository → its mirror target).
    /// </summary>
    internal static HashSet<Guid> UpstreamOf(Guid repositoryId, IReadOnlyDictionary<Guid, Guid?> mirrorTargets)
    {
        var result = new HashSet<Guid> { repositoryId };
        var pending = new Queue<Guid>([repositoryId]);
        while (pending.TryDequeue(out var current))
        {
            foreach (var (source, target) in mirrorTargets)
            {
                if (target == current && result.Add(source))
                    pending.Enqueue(source);
            }
        }
        return result;
    }

    /// <summary>
    /// Picks up to <paramref name="count"/> distinct files (≤ <see cref="MaximumSampleFileBytes"/>)
    /// below <paramref name="root"/> by random descent: from the root, a random entry per level
    /// until a file is reached. Only the folders on those paths are listed, each at most once,
    /// so the cost does not grow with the size of the share. Files in small or shallow folders
    /// are picked somewhat more often than in a uniform sample, which a spot check tolerates.
    /// </summary>
    internal static async Task<List<ResticNode>> SampleFilesAsync(
        Func<string, Task<IReadOnlyList<ResticNode>>> listDirectory, string root, int count, Random random)
    {
        var picked = new Dictionary<string, ResticNode>(StringComparer.Ordinal);
        var listings = new Dictionary<string, List<ResticNode>>(StringComparer.Ordinal);
        // Empty folders end an attempt without a file; the bound keeps a sparse tree finite.
        for (var attempt = 0; attempt < count * 4 && picked.Count < count; attempt++)
        {
            var directory = root;
            for (var depth = 0; depth < 64; depth++)
            {
                if (!listings.TryGetValue(directory, out var children))
                    listings[directory] = children = (await listDirectory(directory))
                        .Where(n => n.IsDirectory || n.Size <= MaximumSampleFileBytes).ToList();
                if (children.Count == 0)
                    break;
                var node = children[random.Next(children.Count)];
                if (!node.IsDirectory)
                {
                    picked.TryAdd(node.Path, node);
                    break;
                }
                directory = node.Path;
            }
        }
        return picked.Values.ToList();
    }

    /// <summary>Failures that concern the repository as a whole, not a single step.</summary>
    public static bool IsRepositoryError(string? code)
        => code is "repo_unreachable" or "repo_not_found" or "wrong_password" or "access_denied" or "tls_error";

    public async Task ExecuteAsync(Guid repositoryId, BackupRunTrigger trigger, Guid? actorUserId, Action<string?, int> report, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repository = await db.BackupRepositories.FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repository is null || repository.State != BackupRepositoryState.Active)
        {
            logger.LogInformation("Maintenance of backup repository {RepositoryId} skipped: not active.", repositoryId);
            return;
        }

        var run = new MaintenanceRun(this, db, repository, trigger, actorUserId, report, ct);
        try
        {
            await run.ExecuteAsync();
        }
        finally
        {
            run.Dispose();
        }
    }

    private sealed class MaintenanceRun(
        BackupMaintenanceExecutor owner,
        ApplicationDbContext db,
        BackupRepository repository,
        BackupRunTrigger trigger,
        Guid? actorUserId,
        Action<string?, int> report,
        CancellationToken ct) : IDisposable
    {
        private ResticTarget? _target;
        private bool _aborted;
        private int _step;
        private int _steps;

        private ResticClient Restic => owner.Client;
        private DateTime Now => owner.Time.GetUtcNow().UtcDateTime;

        public void Dispose() => _target?.Dispose();

        /// <summary>Resolved on first use, after removing stale locks a killed process may have left.</summary>
        private async Task<ResticTarget> TargetAsync()
        {
            if (_target is null)
            {
                _target = await owner.Resolver.ResolveAsync(repository, ct);
                await Restic.UnlockAsync(_target, ct);
            }
            return _target;
        }

        public async Task ExecuteAsync()
        {
            // Jobs of this repository plus jobs of every repository whose points reach it through
            // a mirror chain (A → B → this): copied backup points keep their job tag, so the
            // same retention applies here.
            var sources = UpstreamOf(repository.Id, await db.BackupRepositories.AsNoTracking()
                .Select(r => new { r.Id, r.MirrorTargetId }).ToDictionaryAsync(r => r.Id, r => r.MirrorTargetId, ct)).ToList();
            var retentionJobs = (await db.BackupJobs.AsNoTracking()
                    .Where(j => sources.Contains(j.RepositoryId))
                    .ToListAsync(ct))
                .Where(j => !j.Retention.IsEmpty)
                .ToList();
            var forget = !repository.IsAppendOnly && retentionJobs.Count > 0;
            var mirror = repository.MirrorTargetId is { } mirrorId
                ? await db.BackupRepositories.FirstOrDefaultAsync(r => r.Id == mirrorId, ct)
                : null;
            _steps = (forget ? 1 : 0) + (repository.IsAppendOnly ? 0 : 1) + 1
                     + (repository.TestRestoreSampleSize > 0 ? 1 : 0) + (mirror is not null ? 2 : 0);

            // Backups already apply their job's retention; this pass covers mirrored backup points
            // and retention rules changed since the last backup.
            var removed = 0;
            if (forget)
                await StepAsync(repository, BackupRunType.Forget, async (_, details) =>
                {
                    var target = await TargetAsync();
                    foreach (var job in retentionJobs)
                        removed += await Restic.ForgetAsync(target, job.Id, job.Retention, ct,
                            await BackupJobExecutor.HasSystemPointsAsync(db, repository.Id, job.Id, ct));
                    details.SnapshotsRemoved = removed;
                    details.JobsApplied = retentionJobs.Count;
                    await owner.Jobs.RefreshSnapshotCacheAsync(db, repository.Id, target, ct);
                    return BackupRunStatus.Succeeded;
                });

            // Always: points removed after each backup and data of failed runs are only freed here.
            if (!repository.IsAppendOnly && !_aborted)
                await StepAsync(repository, BackupRunType.Prune, async (_, _) =>
                {
                    await Restic.PruneAsync(await TargetAsync(), ct);
                    return BackupRunStatus.Succeeded;
                });

            if (_aborted)
                return;
            var checkOk = await StepAsync(repository, BackupRunType.Check, (run, details) => CheckAsync(repository, TargetAsync, run, details, readData: true));

            if (checkOk && repository.TestRestoreSampleSize > 0 && !_aborted)
                await StepAsync(repository, BackupRunType.TestRestore, TestRestoreAsync);

            if (checkOk && mirror is not null && !_aborted)
                await MirrorAsync(mirror);
        }

        // ══════════════════════════════════════════
        //  Steps
        // ══════════════════════════════════════════

        /// <summary>
        /// Runs one step as its own run row. Returns true when it succeeded. Failures are
        /// recorded and notified; a repository-level failure (unreachable, wrong password, …)
        /// ends the maintenance, because every further step would fail the same way.
        /// </summary>
        private async Task<bool> StepAsync(
            BackupRepository subject, BackupRunType type, Func<BackupRun, BackupMaintenanceDetails, Task<BackupRunStatus>> body)
        {
            report(BackupUiText.RunType(type), _steps == 0 ? 0 : _step * 100 / _steps);
            _step++;

            var run = new BackupRun
            {
                RepositoryId = subject.Id,
                Type = type,
                Trigger = trigger,
                ActorUserId = actorUserId,
                Status = BackupRunStatus.Running,
                QueuedAtUtc = Now,
                StartedAtUtc = Now,
            };
            db.BackupRuns.Add(run);
            await db.SaveChangesAsync(CancellationToken.None);

            var details = new BackupMaintenanceDetails();
            var previousError = subject.LastErrorCode;
            try
            {
                run.Status = await body(run, details);
                subject.LastReachableAtUtc = Now;
                subject.LastErrorCode = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                run.Status = BackupRunStatus.Cancelled;
                run.ErrorCode = "cancelled";
                await FinishAsync(run, details);
                throw;
            }
            catch (Exception ex)
            {
                run.Status = BackupRunStatus.Failed;
                run.ErrorCode = ex is ResticException re ? re.Code : "unexpected_error";
                if (ex.Data["stderr"] is string stderr && stderr.Length > 0)
                    run.LogExcerpt = Tail(stderr);
                if (ex is not ResticException)
                    owner.Logger.LogError(ex, "Maintenance step {Type} of backup repository {Name} failed unexpectedly.", type, subject.Name);

                // A copy talks to two repositories; its failure cannot be pinned on one of them.
                if (IsRepositoryError(run.ErrorCode) && type != BackupRunType.Copy)
                {
                    _aborted = true;
                    subject.LastErrorCode = run.ErrorCode;
                    if (previousError is null)
                        await owner.Notifications.PublishAsync(NotificationEvents.FileBackupRepositoryUnreachable(subject.Id, subject.Name, run.ErrorCode!));
                }
                else
                {
                    await owner.Notifications.PublishAsync(NotificationEvents.FileBackupMaintenanceFailed(
                        subject.Id, subject.Name, BackupUiText.RunType(type), run.ErrorCode!));
                }
            }

            await FinishAsync(run, details);
            owner.Logger.LogInformation("Maintenance step {Type} of backup repository {Name}: {Status} ({Code}).",
                type, subject.Name, run.Status, run.ErrorCode);
            return run.Status is BackupRunStatus.Succeeded or BackupRunStatus.SucceededWithWarnings;
        }

        private async Task FinishAsync(BackupRun run, BackupMaintenanceDetails details)
        {
            run.FinishedAtUtc = Now;
            run.DetailsJson = details.Serialize();
            await db.SaveChangesAsync(CancellationToken.None);
        }

        /// <summary>
        /// Structural check; the first check of a calendar month also reads the month's twelfth
        /// of the pack data, so every pack is read once a year without a full read each time.
        /// </summary>
        private async Task<BackupRunStatus> CheckAsync(
            BackupRepository subject, Func<Task<ResticTarget>> target, BackupRun run, BackupMaintenanceDetails details, bool readData)
        {
            var zone = owner.Time.LocalTimeZone;
            var local = TimeZoneInfo.ConvertTimeFromUtc(Now, zone);
            var subset = readData && subject.ReadDataMonthly && !SameMonth(subject.LastReadDataAtUtc, local, zone)
                ? $"{local.Month}/12"
                : null;
            var result = await Restic.CheckAsync(await target(), subset, ct);

            subject.LastCheckAtUtc = Now;
            subject.LastCheckOk = result.Ok;
            if (subset is not null)
                subject.LastReadDataAtUtc = Now;
            details.ReadDataSubset = subset;
            if (result.Errors.Count > 0)
                details.CheckErrors = result.Errors.ToList();
            if (result.Ok)
                return BackupRunStatus.Succeeded;

            run.ErrorCode = "check_failed";
            run.LogExcerpt = Tail(result.OutputTail);
            await owner.Notifications.PublishAsync(NotificationEvents.FileBackupCheckFailed(subject.Id, subject.Name, Math.Max(1, result.Errors.Count)));
            return BackupRunStatus.Failed;
        }

        private static bool SameMonth(DateTime? utc, DateTime local, TimeZoneInfo zone)
        {
            if (utc is not { } value)
                return false;
            var then = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), zone);
            return then.Year == local.Year && then.Month == local.Month;
        }

        /// <summary>
        /// Restores a random sample of files from the newest backup point of every share and
        /// compares each with its live file by SHA-256 — but only files whose size and mtime
        /// still match the backup; changed or deleted files prove nothing and are only counted.
        /// </summary>
        private async Task<BackupRunStatus> TestRestoreAsync(BackupRun run, BackupMaintenanceDetails details)
        {
            var target = await TargetAsync();
            var latest = (await db.BackupSnapshots.AsNoTracking()
                    .Where(s => s.RepositoryId == repository.Id && s.Kind == BackupSnapshotKind.Share && s.ShareId != null)
                    .ToListAsync(ct))
                .GroupBy(s => s.ShareId)
                .Select(g => g.MaxBy(s => s.TimeUtc)!)
                .ToList();

            // The sample is spread evenly over the shares.
            var sample = new List<(BackupSnapshot Snapshot, ResticNode Node)>();
            var perShare = latest.Count == 0 ? 0 : (int)Math.Ceiling(repository.TestRestoreSampleSize / (double)latest.Count);
            foreach (var snapshot in latest)
            {
                var root = BackupRestoreExecutor.SourceRoot(snapshot);
                if (root is null)
                    continue;
                var files = await SampleFilesAsync(
                    dir => Restic.ListDirectoryAsync(target, snapshot.SnapshotId, dir, ct), root, perShare, Random.Shared);
                sample.AddRange(files.Select(f => (snapshot, f)));
            }

            var shareIds = latest.Select(s => s.ShareId!.Value).ToList();
            var shares = await db.ShareDefinitions.AsNoTracking().Where(s => shareIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
            int verified = 0, changed = 0;
            var mismatches = new List<string>();
            foreach (var group in sample.GroupBy(i => i.Snapshot))
            {
                ct.ThrowIfCancellationRequested();
                if (!shares.TryGetValue(group.Key.ShareId!.Value, out var share) || !Directory.Exists(share.Path))
                {
                    changed += group.Count();
                    continue;
                }
                // Inside the share: same file system as the live files, hidden from users,
                // excluded from backups and wiped on startup with the restore staging.
                var shareRoot = Path.TrimEndingDirectorySeparator(share.Path);
                var staging = Path.Combine(shareRoot, BackupRestoreExecutor.StagingFolderName, "test-" + run.Id.ToString("N"));
                try
                {
                    BackupRestoreExecutor.EnsureFreeSpace(shareRoot, (long)(group.Sum(i => i.Node.Size) * 1.1));
                    BackupRestoreExecutor.CreatePrivateDirectory(staging);
                    await Restic.RestorePathsAsync(target, group.Key.SnapshotId, group.Select(i => i.Node.Path).ToList(), staging, ct);
                    foreach (var (_, node) in group)
                    {
                        if (!IsUnchanged(node))
                        {
                            changed++;
                            continue;
                        }
                        var restored = Path.Combine(staging, node.Path.TrimStart('/'));
                        if (File.Exists(restored) && await SameHashAsync(node.Path, restored, ct))
                            verified++;
                        else if (!IsUnchanged(node))
                            changed++; // modified while it was being compared
                        else
                            mismatches.Add(node.Path);
                    }
                }
                finally
                {
                    BackupRestoreExecutor.TryDeleteDirectory(staging);
                    try { Directory.Delete(Path.GetDirectoryName(staging)!); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }

            if (await VerifyDatabaseDumpAsync(target, run) is { } dumpOk)
            {
                details.DatabaseDumpOk = dumpOk;
                if (!dumpOk)
                    mismatches.Add(BackupJobExecutor.DatabaseDumpFileName);
            }

            details.FilesVerified = verified;
            details.FilesChanged = changed;
            if (mismatches.Count == 0)
                return BackupRunStatus.Succeeded;

            details.Mismatches = mismatches.Take(MaximumListedFiles).ToList();
            run.ErrorCode = "test_restore_mismatch";
            run.WarningCount = mismatches.Count;
            await owner.Notifications.PublishAsync(NotificationEvents.FileBackupTestRestoreMismatch(repository.Id, repository.Name, details.Mismatches));
            return BackupRunStatus.Failed;
        }

        /// <summary>
        /// The database dump is what a disaster recovery depends on: the dump of the newest
        /// system backup point is read back and must be parseable by <c>pg_restore --list</c>.
        /// Null when the repository holds no system backup point.
        /// </summary>
        private async Task<bool?> VerifyDatabaseDumpAsync(ResticTarget target, BackupRun run)
        {
            var system = (await db.BackupSnapshots.AsNoTracking()
                    .Where(s => s.RepositoryId == repository.Id && s.Kind == BackupSnapshotKind.System)
                    .ToListAsync(ct))
                .MaxBy(s => s.TimeUtc);
            var dumpPath = system is null ? null : BackupRestoreExecutor.DatabaseDumpPath(system);
            if (dumpPath is null)
                return null;

            // Private staging in the backup folder (the dump holds every credential).
            var staging = Path.Combine(owner.Jobs.MetaRoot, "verify-" + run.Id.ToString("N"));
            try
            {
                BackupRestoreExecutor.CreatePrivateDirectory(staging);
                BackupRestoreExecutor.EnsureFreeSpace(staging, (long)(system!.TotalBytes * 1.1));
                var file = Path.Combine(staging, BackupJobExecutor.DatabaseDumpFileName);
                var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var stream = new FileStream(file, options))
                    await Restic.DumpAsync(target, system.SnapshotId, dumpPath, asZipArchive: false, stream, ct);
                return await owner.Database.VerifyDumpAsync(file, ct);
            }
            finally
            {
                BackupRestoreExecutor.TryDeleteDirectory(staging);
            }
        }

        /// <summary>Same size and (to the second) same mtime as in the backup point.</summary>
        private static bool IsUnchanged(ResticNode node)
        {
            var live = new FileInfo(node.Path);
            return live.Exists && live.Length == node.Size && node.ModifiedUtc is { } modified
                   && Math.Abs((live.LastWriteTimeUtc - modified).TotalSeconds) < 1;
        }

        private static async Task<bool> SameHashAsync(string a, string b, CancellationToken ct)
        {
            await using var first = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await using var second = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read);
            var x = await SHA256.HashDataAsync(first, ct);
            var y = await SHA256.HashDataAsync(second, ct);
            return x.AsSpan().SequenceEqual(y);
        }

        /// <summary>Copies new backup points into the mirror, then checks the mirror (structure only).</summary>
        private async Task MirrorAsync(BackupRepository mirror)
        {
            ResticTarget? mirrorTarget = null;
            try
            {
                var copied = await StepAsync(repository, BackupRunType.Copy, async (_, details) =>
                {
                    details.MirrorName = mirror.Name;
                    if (mirror.State != BackupRepositoryState.Active)
                        throw new ResticException("mirror_not_active", "The mirror repository is not active.");
                    ResticBackend.ValidateMirror(repository, owner.Resolver.ReadSecrets(repository), mirror, owner.Resolver.ReadSecrets(mirror));
                    mirrorTarget = await owner.Resolver.ResolveAsync(mirror, ct);
                    await Restic.UnlockAsync(mirrorTarget, ct);
                    var before = await db.BackupSnapshots.CountAsync(s => s.RepositoryId == mirror.Id, ct);
                    await Restic.CopyAsync(mirrorTarget, await TargetAsync(), ct);
                    await owner.Jobs.RefreshSnapshotCacheAsync(db, mirror.Id, mirrorTarget, ct);
                    details.SnapshotsCopied = Math.Max(0, await db.BackupSnapshots.CountAsync(s => s.RepositoryId == mirror.Id, ct) - before);
                    mirror.LastReachableAtUtc = Now;
                    mirror.LastErrorCode = null;
                    return BackupRunStatus.Succeeded;
                });

                if (copied && mirrorTarget is { } t)
                    await StepAsync(mirror, BackupRunType.Check, (run, details) => CheckAsync(mirror, () => Task.FromResult(t), run, details, readData: false));
            }
            finally
            {
                mirrorTarget?.Dispose();
            }
        }

        private static string? Tail(string? text)
            => string.IsNullOrEmpty(text) ? null : text.Length <= MaximumLogExcerpt ? text : text[^MaximumLogExcerpt..];
    }
}

/// <summary>Localized step names for progress and notifications (server UI culture).</summary>
internal static class BackupUiText
{
    public static string RunType(BackupRunType type)
        => Resources.ResourceManager.GetString("Web_Backup_RunType_" + type) ?? type.ToString();
}
