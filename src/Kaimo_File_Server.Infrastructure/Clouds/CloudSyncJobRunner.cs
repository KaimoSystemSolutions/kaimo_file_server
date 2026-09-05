using System.Collections.Concurrent;
using System.Threading.Channels;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Clouds;

/// <summary>
/// Runs cloud syncs off the request/UI thread as durable background jobs. Both
/// the manual "Run now" action and the scheduler enqueue here, so a sync never
/// blocks the Blazor circuit and always shows up in the Running Jobs menu. The
/// runner owns each job's cancellation and lease lifetime, so a disconnected UI
/// session can no longer abandon (and thereby wedge) a running sync.
/// </summary>
public interface ICloudSyncJobRunner
{
    /// <summary>Render-safe snapshot of every queued or running sync job.</summary>
    IReadOnlyList<SyncJobSnapshot> Jobs { get; }

    /// <summary>Raised after the job set or a job's visible state changes.</summary>
    event Action? OnChanged;

    /// <summary>
    /// Queues a sync. If an identical (share, local path) job is already queued or
    /// running, its existing id is returned instead of starting a second one.
    /// </summary>
    Guid Enqueue(Guid shareId, string localPath, Guid runAsUserId, string title, string detail);

    /// <summary>Requests cooperative cancellation for a running/queued job.</summary>
    bool Cancel(Guid jobId);
}

/// <summary>Immutable job view consumed by the Running Jobs menu.</summary>
public sealed record SyncJobSnapshot(
    Guid Id,
    string Title,
    string Detail,
    int Progress,
    DateTimeOffset StartedAt,
    bool IsCancellationRequested);

public sealed class CloudSyncJobRunner(
    IServiceScopeFactory scopeFactory,
    ILogger<CloudSyncJobRunner> logger) : BackgroundService, ICloudSyncJobRunner
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object _gate = new();
    private readonly Dictionary<Guid, JobState> _jobs = [];
    private readonly Dictionary<(Guid ShareId, string LocalPath), Guid> _dedupe = [];
    // Cancels every in-flight/queued job at host shutdown. Per-job tokens are
    // linked to this so no sync outlives the process.
    private readonly CancellationTokenSource _shutdown = new();

    public event Action? OnChanged;

    public IReadOnlyList<SyncJobSnapshot> Jobs
    {
        get
        {
            lock (_gate)
                return _jobs.Values
                    .Select(job => job.ToSnapshot())
                    .OrderBy(job => job.StartedAt)
                    .ToArray();
        }
    }

    public Guid Enqueue(Guid shareId, string localPath, Guid runAsUserId, string title, string detail)
    {
        var key = (shareId, CloudSyncPaths.Normalize(localPath));
        Guid jobId;
        lock (_gate)
        {
            if (_dedupe.TryGetValue(key, out Guid existing))
                return existing;

            jobId = Guid.NewGuid();
            _jobs[jobId] = new JobState(
                jobId, shareId, localPath, runAsUserId, title, detail, key,
                CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
            _dedupe[key] = jobId;
        }

        _queue.Writer.TryWrite(jobId);
        NotifyChanged();
        return jobId;
    }

    public bool Cancel(Guid jobId)
    {
        JobState? job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out job) || job.CancelRequested)
                return false;
            job.CancelRequested = true;
        }

        job.Cancellation.Cancel();
        NotifyChanged();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var registration = stoppingToken.Register(static state =>
            ((CancellationTokenSource)state!).Cancel(), _shutdown);
        try
        {
            await foreach (Guid jobId in _queue.Reader.ReadAllAsync(stoppingToken))
                await RunJobAsync(jobId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    private async Task RunJobAsync(Guid jobId)
    {
        JobState? job;
        lock (_gate)
            _jobs.TryGetValue(jobId, out job);
        if (job is null)
            return;

        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            var execution = scope.ServiceProvider.GetRequiredService<ICloudSyncExecutionService>();
            var users = scope.ServiceProvider.GetRequiredService<IUserContextFactory>();

            var actor = await users.CreateByUserIdAsync(job.RunAsUserId);
            if (actor is null || !actor.User.IsEnabled)
            {
                logger.LogWarning(
                    "Cloud sync job for {ShareId}/{LocalPath} skipped: user {UserId} is unavailable or disabled.",
                    job.ShareId, job.LocalPath, job.RunAsUserId);
                return;
            }

            void Report(string? message, int progress)
            {
                lock (_gate)
                {
                    if (message is not null)
                        job.Detail = message;
                    job.Progress = Math.Clamp(progress, 0, 100);
                }
                NotifyChanged();
            }

            CloudSyncExecutionResult result = await execution.RunAsync(
                job.ShareId, job.LocalPath, actor, Report, job.Cancellation.Token);
            if (result != CloudSyncExecutionResult.Completed)
                logger.LogInformation(
                    "Cloud sync job for {ShareId}/{LocalPath} ended with {Result}.",
                    job.ShareId, job.LocalPath, result);
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
            // Cooperative cancellation from the menu or host shutdown.
        }
        catch (Exception exception)
        {
            // The execution service already persisted the failure (LastErrorCode),
            // which drives the health surfaces; this loop just logs and moves on.
            logger.LogError(
                exception, "Cloud sync job failed for {ShareId}/{LocalPath}.",
                job.ShareId, job.LocalPath);
        }
        finally
        {
            Complete(job);
        }
    }

    private void Complete(JobState job)
    {
        lock (_gate)
        {
            if (!_jobs.Remove(job.Id))
                return;
            if (_dedupe.TryGetValue(job.Key, out Guid owner) && owner == job.Id)
                _dedupe.Remove(job.Key);
        }

        job.Cancellation.Dispose();
        NotifyChanged();
    }

    public override void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        base.Dispose();
    }

    private void NotifyChanged() => OnChanged?.Invoke();

    private sealed class JobState(
        Guid id,
        Guid shareId,
        string localPath,
        Guid runAsUserId,
        string title,
        string detail,
        (Guid, string) key,
        CancellationTokenSource cancellation)
    {
        public Guid Id { get; } = id;
        public Guid ShareId { get; } = shareId;
        public string LocalPath { get; } = localPath;
        public Guid RunAsUserId { get; } = runAsUserId;
        public string Title { get; } = title;
        public string Detail { get; set; } = detail;
        public (Guid, string) Key { get; } = key;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public int Progress { get; set; }
        public bool CancelRequested { get; set; }
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public SyncJobSnapshot ToSnapshot()
            => new(Id, Title, Detail, Progress, StartedAt, CancelRequested);
    }
}
