using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace Kaimo_File_Server.Infrastructure.Clouds;

/// <summary>Wakes the scheduler when a mapping is changed through the web UI.</summary>
public sealed class CloudSyncSchedulerSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    public void Wake() => _channel.Writer.TryWrite(true);

    public async Task<bool> WaitAsync(
        TimeSpan delay,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task<bool> signalTask = _channel.Reader
            .WaitToReadAsync(waitCancellation.Token).AsTask();
        Task delayTask = Task.Delay(delay, timeProvider, waitCancellation.Token);
        Task completed = await Task.WhenAny(signalTask, delayTask);
        bool signaled = completed == signalTask && await signalTask;

        await waitCancellation.CancelAsync();
        if (signaled)
        {
            while (_channel.Reader.TryRead(out _))
            {
            }
        }

        return signaled;
    }
}

/// <summary>
/// Evaluates enabled cloud-sync schedules at the configured interval and starts
/// mappings selected for the current local server hour. A successful manual or
/// scheduled run is eligible again after its configured interval has elapsed.
/// </summary>
public sealed class CloudSyncSchedulerService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    CloudSyncSchedulerSignal signal,
    ILogger<CloudSyncSchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(
        CloudSyncSchedule.DefaultIntervalSeconds);
    private readonly Dictionary<ScheduleKey, DateTimeOffset> _lastEvaluations = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool forceEvaluation = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay = await CheckNowSafelyAsync(forceEvaluation, stoppingToken);
            forceEvaluation = await signal.WaitAsync(delay, timeProvider, stoppingToken);
        }
    }

    /// <summary>
    /// Runs one schedule evaluation and returns the delay until the next entry
    /// becomes due for evaluation.
    /// </summary>
    public async Task<TimeSpan> CheckNowAsync(
        bool forceEvaluation = false,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset localNow = timeProvider.GetLocalNow();
        DateTimeOffset utcNow = timeProvider.GetUtcNow();
        TimeSpan nextDelay = IdleInterval;
        var activeKeys = new HashSet<ScheduleKey>();
        using var scope = scopeFactory.CreateScope();
        var syncDefinitions = scope.ServiceProvider.GetRequiredService<ISyncDefinitionRepository>();
        var legacyMigration = scope.ServiceProvider.GetRequiredService<ILegacyCloudSyncMigrationService>();
        var execution = scope.ServiceProvider.GetRequiredService<ICloudSyncExecutionService>();
        var users = scope.ServiceProvider.GetRequiredService<IUserContextFactory>();
        var actors = new Dictionary<Guid, UserContext?>();

        await legacyMigration.EnsureMigratedAsync(cancellationToken);
        foreach (SyncDefinitionScheduleEntry entry in
                 await syncDefinitions.GetEnabledScheduledAsync(cancellationToken))
        {
            SyncDefinition definition = entry.Definition;
            string localPath = definition.LocalPath;
                cancellationToken.ThrowIfCancellationRequested();
                CloudSyncSchedule? schedule = definition.Schedule;
                if (schedule is null || !schedule.IsEnabled)
                    continue;

                var key = new ScheduleKey(definition.LocalShareId, localPath);
                activeKeys.Add(key);
                TimeSpan interval = TimeSpan.FromSeconds(
                    schedule.GetEffectiveIntervalSeconds());
                if (!forceEvaluation &&
                    _lastEvaluations.TryGetValue(key, out DateTimeOffset lastEvaluation))
                {
                    TimeSpan elapsed = utcNow - lastEvaluation;
                    if (elapsed < interval)
                    {
                        nextDelay = Min(nextDelay, interval - elapsed);
                        continue;
                    }
                }

                _lastEvaluations[key] = utcNow;
                nextDelay = Min(nextDelay, interval);
                if (!IsDue(schedule, entry.LastSuccessfulRunAtUtc, localNow, timeProvider.LocalTimeZone))
                    continue;

                Guid? runAsUserId = definition.RunAsUserId;
                if (runAsUserId is null)
                {
                    logger.LogWarning(
                        "Scheduled cloud sync {SyncDefinitionId} has no execution user and was skipped.",
                        definition.Id);
                    continue;
                }

                if (!actors.TryGetValue(runAsUserId.Value, out UserContext? actor))
                {
                    actor = await users.CreateByUserIdAsync(runAsUserId.Value);
                    actors[runAsUserId.Value] = actor;
                }

                if (actor is null || !actor.User.IsEnabled)
                {
                    logger.LogWarning(
                        "Scheduled cloud sync {SyncDefinitionId} was skipped because user {UserId} is unavailable or disabled.",
                        definition.Id, runAsUserId.Value);
                    continue;
                }

                try
                {
                    CloudSyncExecutionResult result = await execution.RunAsync(
                        definition.LocalShareId,
                        localPath,
                        actor,
                        cancellationToken: cancellationToken);
                    if (result == CloudSyncExecutionResult.Completed)
                    {
                        logger.LogInformation(
                            "Scheduled cloud sync completed for {ShareId}/{LocalPath}.",
                            definition.LocalShareId, localPath);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Scheduled cloud sync failed for {ShareId}/{LocalPath}.",
                        definition.LocalShareId, localPath);
                }
        }

        foreach (ScheduleKey staleKey in _lastEvaluations.Keys
                     .Where(key => !activeKeys.Contains(key)).ToArray())
        {
            _lastEvaluations.Remove(staleKey);
        }

        return nextDelay <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : nextDelay;
    }

    public static bool IsDue(
        CloudSyncSchedule schedule,
        DateTime? lastSyncUtc,
        DateTimeOffset localNow,
        TimeZoneInfo localTimeZone)
    {
        if (!schedule.IsEnabled || !schedule.IsActive(localNow.DayOfWeek, localNow.Hour))
            return false;

        if (lastSyncUtc is null)
            return true;

        DateTime utc = lastSyncUtc.Value.Kind switch
        {
            DateTimeKind.Utc => lastSyncUtc.Value,
            DateTimeKind.Local => lastSyncUtc.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(lastSyncUtc.Value, DateTimeKind.Utc)
        };
        DateTimeOffset lastSync = new(utc);
        return localNow.ToUniversalTime() - lastSync
               >= TimeSpan.FromSeconds(schedule.GetEffectiveIntervalSeconds());
    }

    private async Task<TimeSpan> CheckNowSafelyAsync(
        bool forceEvaluation,
        CancellationToken stoppingToken)
    {
        try
        {
            return await CheckNowAsync(forceEvaluation, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
            return IdleInterval;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cloud-sync schedule evaluation failed.");
            return IdleInterval;
        }
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right)
        => left <= right ? left : right;

    private readonly record struct ScheduleKey(Guid ShareId, string LocalPath);
}
