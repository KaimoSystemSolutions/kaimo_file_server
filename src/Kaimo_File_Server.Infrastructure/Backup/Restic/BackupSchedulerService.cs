using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>
/// Enqueues scheduled backup runs (Web process only, single instance). The handled slot is
/// persisted before the run is queued, so a crash between the two can never cause a double
/// run; at worst one slot is skipped. Missed slots collapse into one catch-up run.
/// </summary>
public sealed class BackupSchedulerService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IBackupRunner runner,
    DemoModeOptions demo,
    TimeProvider time,
    ILogger<BackupSchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

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
                due.Add((job.Id, now - slot.Value > 2 * PollInterval ? BackupRunTrigger.CatchUp : BackupRunTrigger.Scheduled));
            }

            if (due.Count == 0)
                return;
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
    }
}
