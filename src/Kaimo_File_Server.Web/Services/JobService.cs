namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Tracks cancellable work started by the current UI session. The service is
/// provider-neutral so uploads, cloud syncs, and future background operations
/// can use the same job menu and cancellation behavior.
/// </summary>
public sealed class JobService : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ActiveJob> _jobs = [];

    /// <summary>
    /// Raised after the active-job collection or a job's visible state changes.
    /// Subscribers must marshal the callback to their own UI context.
    /// </summary>
    public event Action? OnChanged;

    /// <summary>
    /// Returns an immutable, start-time-ordered snapshot of all work that is
    /// still active in the current UI session. A snapshot prevents Razor from
    /// enumerating the mutable backing collection while workers report progress.
    /// </summary>
    public IReadOnlyList<JobSnapshot> Jobs
    {
        get
        {
            lock (_gate)
            {
                return _jobs.Values
                    .Select(job => job.ToSnapshot())
                    .OrderBy(job => job.StartedAt)
                    .ToArray();
            }
        }
    }

    /// <summary>
    /// Registers a new cancellable operation and returns the handle owned by
    /// that operation. The caller must complete or dispose the handle in a
    /// finally block so finished jobs disappear from the menu.
    /// </summary>
    /// <param name="title">Stable, user-facing name shown in the job list.</param>
    /// <param name="detail">Initial description of the current work step.</param>
    /// <param name="kind">Provider-neutral category used for future filtering or icons.</param>
    public JobHandle Start(string title, string detail, string kind)
    {
        var job = new ActiveJob(
            Guid.NewGuid().ToString("N"),
            title,
            detail,
            kind,
            DateTimeOffset.UtcNow,
            new CancellationTokenSource());

        lock (_gate)
            _jobs.Add(job.Id, job);

        NotifyChanged();
        return new JobHandle(this, job.Id, job.Cancellation.Token);
    }

    /// <summary>
    /// Requests cooperative cancellation for an active job. The job remains in
    /// the menu as "cancelling" until its owner observes the token and completes
    /// the handle; this avoids falsely presenting a still-running transfer as gone.
    /// </summary>
    /// <returns><see langword="true"/> only for the first valid cancellation request.</returns>
    public bool Cancel(string id)
    {
        ActiveJob? job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out job) || job.IsCancellationRequested)
                return false;

            job.IsCancellationRequested = true;
        }

        // Cancellation callbacks may execute user code, so they must run
        // outside the collection lock.
        job.Cancellation.Cancel();
        NotifyChanged();
        return true;
    }

    /// <summary>
    /// Applies a partial progress update. Null values leave their corresponding
    /// fields unchanged, and percentages are clamped for valid progress markup.
    /// </summary>
    internal void Update(string id, string? detail, int? progress)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job))
                return;

            if (detail is not null)
                job.Detail = detail;
            if (progress is not null)
                job.Progress = Math.Clamp(progress.Value, 0, 100);
        }

        NotifyChanged();
    }

    /// <summary>
    /// Removes completed work and releases its cancellation source. Completion
    /// is intentionally idempotent because both explicit cleanup and disposal
    /// may occur in asynchronous operation owners.
    /// </summary>
    internal void Complete(string id)
    {
        ActiveJob? job;
        lock (_gate)
        {
            if (!_jobs.Remove(id, out job))
                return;
        }

        job.Cancellation.Dispose();
        NotifyChanged();
    }

    /// <summary>
    /// Cancels all jobs when the UI scope ends and releases their token sources.
    /// This prevents transfers from surviving a disconnected user session.
    /// </summary>
    public void Dispose()
    {
        ActiveJob[] jobs;
        lock (_gate)
        {
            jobs = _jobs.Values.ToArray();
            _jobs.Clear();
        }

        foreach (var job in jobs)
        {
            job.Cancellation.Cancel();
            job.Cancellation.Dispose();
        }
    }

    /// <summary>Notifies menu components after state has been committed.</summary>
    private void NotifyChanged() => OnChanged?.Invoke();

    private sealed class ActiveJob(
        string id,
        string title,
        string detail,
        string kind,
        DateTimeOffset startedAt,
        CancellationTokenSource cancellation)
    {
        public string Id { get; } = id;
        public string Title { get; } = title;
        public string Detail { get; set; } = detail;
        public string Kind { get; } = kind;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public int Progress { get; set; }
        public bool IsCancellationRequested { get; set; }

        /// <summary>Copies mutable worker state into a render-safe value object.</summary>
        public JobSnapshot ToSnapshot()
            => new(
                Id,
                Title,
                Detail,
                Kind,
                StartedAt,
                Progress,
                IsCancellationRequested);
    }
}

/// <summary>
/// Immutable representation consumed by UI components. It intentionally does
/// not expose the cancellation source or any mutable job implementation state.
/// </summary>
public sealed record JobSnapshot(
    string Id,
    string Title,
    string Detail,
    string Kind,
    DateTimeOffset StartedAt,
    int Progress,
    bool IsCancellationRequested);

/// <summary>
/// Gives a job owner the token and narrowly scoped update/complete operations.
/// Completing twice is safe, which keeps async finally blocks straightforward.
/// </summary>
public sealed class JobHandle : IDisposable
{
    private readonly JobService _service;
    private int _completed;

    internal JobHandle(JobService service, string id, CancellationToken cancellationToken)
    {
        _service = service;
        Id = id;
        CancellationToken = cancellationToken;
    }

    /// <summary>Unique identifier shared by the toast and job-menu actions.</summary>
    public string Id { get; }

    /// <summary>
    /// Token that operation code must pass through every cancellable layer,
    /// including provider HTTP requests.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Updates the visible step and/or progress without replacing the job.</summary>
    public void Update(string? detail = null, int? progress = null)
        => _service.Update(Id, detail, progress);

    /// <summary>Requests the same cancellation used by the global job menu.</summary>
    public bool Cancel() => _service.Cancel(Id);

    /// <summary>Marks the operation finished and removes it from the active list.</summary>
    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
            _service.Complete(Id);
    }

    /// <summary>Ensures a using declaration always removes the finished job.</summary>
    public void Dispose() => Complete();
}
