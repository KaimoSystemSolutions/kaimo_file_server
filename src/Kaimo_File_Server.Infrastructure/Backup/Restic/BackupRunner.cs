using System.Threading.Channels;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>Render-safe view of a queued or running backup run (running-jobs menu).</summary>
public sealed record BackupRunSnapshot(
    Guid RunId,
    Guid JobId,
    string Title,
    string? Detail,
    int Progress,
    DateTimeOffset QueuedAt,
    bool IsRunning,
    bool IsCancellationRequested);

/// <summary>Queues backup runs and executes them off the UI thread (Web process only).</summary>
public interface IBackupRunner
{
    IReadOnlyList<BackupRunSnapshot> Runs { get; }
    event Action? OnChanged;

    /// <summary>
    /// Queues a run of the job and returns its run id. If the job is already queued or
    /// running, the existing run id is returned instead of starting a second one.
    /// </summary>
    Task<Guid> EnqueueJobAsync(Guid jobId, BackupRunTrigger trigger, Guid? actorUserId, CancellationToken ct = default);

    bool Cancel(Guid runId);
}

/// <summary>
/// Single-instance runner modelled on the cloud sync job runner. Runs are persisted as
/// <see cref="BackupRun"/> rows before they are queued, so a restart turns lost queue
/// entries into "interrupted" runs instead of silently dropping them.
/// </summary>
public sealed class BackupRunner(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    BackupJobExecutor executor,
    ResticTargetResolver resolver,
    DemoModeOptions demo,
    TimeProvider time,
    ILogger<BackupRunner> logger) : BackgroundService, IBackupRunner
{
    // ponytail: one global serial backup lane; per-repository parallelism if throughput matters.
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object _gate = new();
    private readonly Dictionary<Guid, RunState> _runs = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _recovered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private const long ProgressNotifyIntervalMs = 500;

    public event Action? OnChanged;

    public IReadOnlyList<BackupRunSnapshot> Runs
    {
        get
        {
            lock (_gate)
                return _runs.Values.Select(r => r.ToSnapshot()).OrderBy(r => r.QueuedAt).ToArray();
        }
    }

    public async Task<Guid> EnqueueJobAsync(Guid jobId, BackupRunTrigger trigger, Guid? actorUserId, CancellationToken ct = default)
    {
        if (demo.ReadOnly)
            throw new ReadOnlyDemoException();
        // Never create a queued row before startup recovery ran, or it would be marked interrupted.
        await _recovered.Task.WaitAsync(ct);

        lock (_gate)
        {
            var existing = _runs.Values.FirstOrDefault(r => r.JobId == jobId);
            if (existing is not null)
                return existing.RunId;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var job = await db.BackupJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct)
                  ?? throw new ResticException("job_missing", "The backup job no longer exists.");
        var run = new BackupRun
        {
            RepositoryId = job.RepositoryId,
            JobId = job.Id,
            Type = BackupRunType.Backup,
            Trigger = trigger,
            ActorUserId = actorUserId,
            QueuedAtUtc = time.GetUtcNow().UtcDateTime,
        };

        lock (_gate)
        {
            // Re-check under the lock: a concurrent caller may have queued the job meanwhile.
            var existing = _runs.Values.FirstOrDefault(r => r.JobId == jobId);
            if (existing is not null)
                return existing.RunId;
            _runs[run.Id] = new RunState(run.Id, job.Id, job.Name,
                CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token), time.GetUtcNow());
        }

        try
        {
            db.BackupRuns.Add(run);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            lock (_gate) _runs.Remove(run.Id);
            throw;
        }

        _queue.Writer.TryWrite(run.Id);
        NotifyChanged();
        return run.Id;
    }

    public bool Cancel(Guid runId)
    {
        RunState? state;
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out state) || state.CancelRequested)
                return false;
            state.CancelRequested = true;
        }
        state.Cancellation.Cancel();
        NotifyChanged();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var registration = stoppingToken.Register(static s => ((CancellationTokenSource)s!).Cancel(), _shutdown);
        try
        {
            await RecoverAsync(stoppingToken);
        }
        finally
        {
            _recovered.TrySetResult();
        }

        try
        {
            await foreach (var runId in _queue.Reader.ReadAllAsync(stoppingToken))
                await RunAsync(runId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    /// <summary>Runs from a previous process can never finish; also clears leftover temp files.</summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            resolver.WipeTempRoot();
            executor.WipeMetaRoot();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var now = time.GetUtcNow().UtcDateTime;
            var stale = await db.BackupRuns
                .Where(r => r.Status == BackupRunStatus.Queued || r.Status == BackupRunStatus.Running)
                .ToListAsync(ct);
            foreach (var run in stale)
            {
                run.Status = BackupRunStatus.Interrupted;
                run.ErrorCode = "interrupted";
                run.FinishedAtUtc = now;
            }
            if (stale.Count > 0)
                await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Recovering interrupted backup runs failed.");
        }
    }

    private async Task RunAsync(Guid runId)
    {
        RunState? state;
        lock (_gate)
            _runs.TryGetValue(runId, out state);
        if (state is null)
            return;

        try
        {
            if (state.Cancellation.IsCancellationRequested)
            {
                await MarkAsync(runId, BackupRunStatus.Cancelled, "cancelled");
                return;
            }

            lock (_gate) state.IsRunning = true;
            NotifyChanged();

            long lastNotify = 0;
            await executor.ExecuteAsync(runId, (detail, progress) =>
            {
                lock (_gate)
                {
                    if (detail is not null) state.Detail = detail;
                    state.Progress = Math.Clamp(progress, 0, 100);
                }
                var now = Environment.TickCount64;
                if (now - lastNotify < ProgressNotifyIntervalMs && progress is > 0 and < 100)
                    return;
                lastNotify = now;
                NotifyChanged();
            }, state.Cancellation.Token);
        }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
        {
            await MarkAsync(runId, BackupRunStatus.Cancelled, "cancelled");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backup run {RunId} crashed.", runId);
            await MarkAsync(runId, BackupRunStatus.Failed, "unexpected_error");
        }
        finally
        {
            lock (_gate) _runs.Remove(runId);
            state.Cancellation.Dispose();
            NotifyChanged();
        }
    }

    private async Task MarkAsync(Guid runId, BackupRunStatus status, string code)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var run = await db.BackupRuns.FirstOrDefaultAsync(r => r.Id == runId);
            if (run is null || run.Status is not (BackupRunStatus.Queued or BackupRunStatus.Running))
                return;
            run.Status = status;
            run.ErrorCode = code;
            run.FinishedAtUtc = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not mark backup run {RunId} as {Status}.", runId, status);
        }
    }

    public override void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        base.Dispose();
    }

    private void NotifyChanged() => OnChanged?.Invoke();

    private sealed class RunState(Guid runId, Guid jobId, string title, CancellationTokenSource cancellation, DateTimeOffset queuedAt)
    {
        public Guid RunId { get; } = runId;
        public Guid JobId { get; } = jobId;
        public string Title { get; } = title;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public DateTimeOffset QueuedAt { get; } = queuedAt;
        public string? Detail { get; set; }
        public int Progress { get; set; }
        public bool IsRunning { get; set; }
        public bool CancelRequested { get; set; }

        public BackupRunSnapshot ToSnapshot()
            => new(RunId, JobId, Title, Detail, Progress, QueuedAt, IsRunning, CancelRequested);
    }
}
