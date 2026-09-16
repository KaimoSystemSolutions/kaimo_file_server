using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace Kaimo_File_Server.Infrastructure.Backup;

/// <summary>Wakes the backup scheduler when settings change through the Web UI.</summary>
public sealed class BackupSchedulerSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    public void Wake() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task signalTask = _channel.Reader.WaitToReadAsync(waitCancellation.Token).AsTask();
        Task delayTask = Task.Delay(delay, timeProvider, waitCancellation.Token);
        await Task.WhenAny(signalTask, delayTask);
        await waitCancellation.CancelAsync();
        while (_channel.Reader.TryRead(out _)) { }
    }
}

/// <summary>
/// Runs a single database backup per day inside the configured local-time
/// window. Registered only in the database-owning Host process. If the server
/// was off at the start of the window, the backup runs on the next tick that
/// still falls inside the window (catch-up), because "already ran today" is
/// derived from the newest scheduled backup on disk.
/// </summary>
public sealed class DatabaseBackupSchedulerService(
    IDatabaseBackupService backupService,
    IBackupSettingsStore settingsStore,
    TimeProvider timeProvider,
    BackupSchedulerSignal signal,
    ILogger<DatabaseBackupSchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    /// <summary>How often pruning runs, independent of whether a backup is due.</summary>
    public static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private DateTimeOffset? _lastPruneLocal;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceSafelyAsync(stoppingToken);
            await signal.WaitAsync(PollInterval, timeProvider, stoppingToken);
        }
    }

    private async Task RunOnceSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await settingsStore.GetAsync();
            var localNow = timeProvider.GetLocalNow();

            // Pruning runs on its own hourly cadence, BEFORE and independent of the
            // backup. Otherwise pruning only ran after a successful backup: disabled
            // backups never pruned (pre-migration backups accumulated forever) and a
            // disk-full backup failure meant the condition could never self-heal. A
            // pruning failure is caught here so it never blocks the backup below.
            if (ShouldPrune(localNow, _lastPruneLocal))
            {
                try
                {
                    await backupService.PruneAsync(
                        settings, pruneScheduled: settings.Enabled, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Backup pruning failed.");
                }
                _lastPruneLocal = localNow;
            }

            if (!IsDue(settings, localNow, ListLastScheduledLocalDate()))
                return;

            await backupService.CreateBackupAsync(BackupTrigger.Scheduled, cancellationToken);
            _lastPruneLocal = null; // prune again right after a fresh backup
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled database backup failed.");
        }
    }

    /// <summary>
    /// Pure decision: should pruning run now? True on the first tick and once per
    /// <see cref="PruneInterval"/> thereafter. Exposed for testing.
    /// </summary>
    public static bool ShouldPrune(DateTimeOffset localNow, DateTimeOffset? lastPruneLocal)
        => lastPruneLocal is null || (localNow - lastPruneLocal.Value) >= PruneInterval;

    /// <summary>
    /// Pure decision: is a scheduled backup due right now? Exposed for testing.
    /// </summary>
    public static bool IsDue(BackupSettings settings, DateTimeOffset localNow, DateOnly? lastScheduledDate)
    {
        if (!settings.Enabled)
            return false;

        if (!IsWithinWindow(settings.WindowStart, settings.WindowEnd, TimeOnly.FromDateTime(localNow.DateTime)))
            return false;

        var today = DateOnly.FromDateTime(localNow.DateTime);
        return lastScheduledDate != today;
    }

    /// <summary>
    /// Whether <paramref name="now"/> falls inside the daily window, handling a
    /// window that wraps past midnight (start &gt; end).
    /// </summary>
    public static bool IsWithinWindow(TimeOnly start, TimeOnly end, TimeOnly now)
        => start <= end
            ? now >= start && now <= end
            : now >= start || now <= end;

    private DateOnly? ListLastScheduledLocalDate()
    {
        var newest = backupService.ListBackups()
            .Where(b => b.Trigger == BackupTrigger.Scheduled)
            .Select(b => (DateOnly?)DateOnly.FromDateTime(b.CreatedAtLocal.DateTime))
            .FirstOrDefault();
        return newest;
    }
}
