using System.Globalization;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>
/// Enqueues scheduled backup runs and repository maintenance (Web process only, single
/// instance). The handled slot is persisted before the run is queued, so a crash between the
/// two can never cause a double run; at worst one slot is skipped. Missed slots collapse into
/// one catch-up run. Also raises the "stale" alert for scheduled jobs without recent success.
/// </summary>
public sealed class BackupSchedulerService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IBackupRunner runner,
    INotificationPublisher notifications,
    DemoModeOptions demo,
    TimeProvider time,
    ILogger<BackupSchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StaleAlertInterval = TimeSpan.FromHours(24);

    // ponytail: in-memory alert throttle; a restart re-alerts a stale job once (the mail rule throttles too).
    private readonly Dictionary<Guid, DateTime> _staleAlerts = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (demo.ReadOnly)
            return;
        using var timer = new PeriodicTimer(PollInterval, time);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Backup scheduler tick failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        List<(Guid JobId, BackupRunTrigger Trigger)> due = [];
        List<(Guid RepositoryId, string Name, BackupRunTrigger Trigger)> maintenance = [];

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var activeRepositories = await db.BackupRepositories
                .Where(r => r.State == BackupRepositoryState.Active)
                .Select(r => r.Id)
                .ToListAsync(ct);
            var jobs = await db.BackupJobs
                .Where(j => j.Enabled && activeRepositories.Contains(j.RepositoryId))
                .ToListAsync(ct);

            foreach (var job in jobs)
            {
                var slot = BackupScheduleCalculator.FindDueSlot(
                    job.Schedule, now, time.LocalTimeZone, job.LastScheduledSlotUtc ?? job.CreatedAtUtc,
                    BackupScheduleCalculator.DefaultMaxCatchUp);
                if (slot is null)
                    continue;
                job.LastScheduledSlotUtc = slot;
                // More than two poll intervals late means the server was not running at the slot.
                due.Add((job.Id, TriggerFor(slot.Value, now)));
            }

            var repositories = await db.BackupRepositories
                .Where(r => r.State == BackupRepositoryState.Active)
                .ToListAsync(ct);
            var started = false;
            foreach (var repository in repositories)
            {
                // No handled slot yet (repositories from before maintenance existed): start
                // counting now instead of catching up an exclusive prune right after an update.
                if (repository.LastMaintenanceSlotUtc is null)
                {
                    repository.LastMaintenanceSlotUtc = now;
                    started = true;
                    continue;
                }
                var slot = BackupScheduleCalculator.FindDueSlot(
                    repository.MaintenanceSchedule, now, time.LocalTimeZone, repository.LastMaintenanceSlotUtc.Value,
                    BackupScheduleCalculator.DefaultMaxCatchUp);
                if (slot is null)
                    continue;
                repository.LastMaintenanceSlotUtc = slot;
                maintenance.Add((repository.Id, repository.Name, TriggerFor(slot.Value, now)));
            }

            if (due.Count > 0 || maintenance.Count > 0 || started)
                await db.SaveChangesAsync(ct);
        }

        foreach (var (jobId, trigger) in due)
        {
            try
            {
                await runner.EnqueueJobAsync(jobId, trigger, actorUserId: null, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not enqueue scheduled backup job {JobId}.", jobId);
            }
        }

        // Queued after the backups of the same tick: retention and checks see the new points.
        foreach (var (repositoryId, name, trigger) in maintenance)
        {
            try
            {
                var title = string.Format(CultureInfo.CurrentCulture,
                    Resources.ResourceManager.GetString("Web_Backup_Maintenance_JobTitle") ?? "Maintenance {0}", name);
                await runner.EnqueueMaintenanceAsync(repositoryId, title, trigger, actorUserId: null, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not enqueue maintenance of backup repository {RepositoryId}.", repositoryId);
            }
        }

        // Last and isolated: the handled slots are already persisted, so nothing here may
        // keep their runs from being queued.
        try
        {
            await AlertStaleJobsAsync(now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Checking backup jobs for missing successful runs failed.");
        }
    }

    // More than two poll intervals late means the server was not running at the slot.
    private static BackupRunTrigger TriggerFor(DateTime slot, DateTime now)
        => now - slot > 2 * PollInterval ? BackupRunTrigger.CatchUp : BackupRunTrigger.Scheduled;

    private async Task AlertStaleJobsAsync(DateTime now, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var jobs = await db.BackupJobs.AsNoTracking().Where(j => j.Enabled && j.AlertAfterDays > 0).ToListAsync(ct);
        foreach (var job in jobs.Where(j => IsStale(j, now)))
        {
            if (_staleAlerts.TryGetValue(job.Id, out var last) && now - last < StaleAlertInterval)
                continue;
            _staleAlerts[job.Id] = now;
            var repositoryName = await db.BackupRepositories.Where(r => r.Id == job.RepositoryId).Select(r => r.Name).FirstOrDefaultAsync(ct) ?? "-";
            var lastSuccess = job.LastSuccessAtUtc is { } success
                ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(success, DateTimeKind.Utc), time.LocalTimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "-";
            await notifications.PublishAsync(NotificationEvents.FileBackupStale(
                job.Id, job.Name, repositoryName, job.CreatedByUserId, lastSuccess, EffectiveAlertDays(job)), ct);
        }
        foreach (var id in _staleAlerts.Keys.Where(id => jobs.All(j => j.Id != id || !IsStale(j, now))).ToList())
            _staleAlerts.Remove(id);
    }

    /// <summary>
    /// A scheduled, enabled job without a successful run for <see cref="EffectiveAlertDays"/>,
    /// counted from its last success or its last configuration change (creation, re-enabling,
    /// new schedule), whichever is later. Manual-only jobs never go stale.
    /// </summary>
    internal static bool IsStale(BackupJob job, DateTime nowUtc)
    {
        if (!job.Enabled || job.AlertAfterDays <= 0 || job.Schedule.IsEmpty)
            return false;
        var since = job.LastSuccessAtUtc is { } success && success > job.UpdatedAtUtc ? success : job.UpdatedAtUtc;
        return since < nowUtc - TimeSpan.FromDays(EffectiveAlertDays(job));
    }

    /// <summary>
    /// The configured threshold, but never less than the longest gap between two scheduled
    /// days plus one: a weekly job is not overdue before it missed its weekly run.
    /// </summary>
    internal static int EffectiveAlertDays(BackupJob job)
        => Math.Max(job.AlertAfterDays, BackupScheduleCalculator.LongestGapDays(job.Schedule) + 1);
}
