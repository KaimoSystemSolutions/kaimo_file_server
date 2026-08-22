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
            var lastScheduledDate = ListLastScheduledLocalDate();

            if (!IsDue(settings, localNow, lastScheduledDate))
                return;

            await backupService.CreateBackupAsync(BackupTrigger.Scheduled, cancellationToken);
            await backupService.PruneAsync(settings, cancellationToken);
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
