using Kaimo_File_Server.Core.Logging;

namespace Kaimo_File_Server.Search;

public partial class SearchServiceRouter
{
    // ══════════════════════════════════════════
    //  ISearchAdminService — settings page
    // ══════════════════════════════════════════

    public Task<SearchEngineState> GetStateAsync(CancellationToken ct = default)
        => ResolveStateAsync(forceFresh: true, ct);

    public Task SetElasticEnabledAsync(bool enabled, CancellationToken ct = default)
        => SetEngineAsync(enabled ? SearchEngine.Elasticsearch : SearchEngine.Filename, ct);

    public async Task SetEngineAsync(SearchEngine engine, CancellationToken ct = default)
    {
        await _configStore.SetEngineAsync(engine);
        InvalidateState();

        // A reindex writes into the engine it was started for; once that engine is no longer
        // selected, its writes would only fill an index nobody reads.
        lock (_reindexGate)
        {
            if (_reindexProgress.Running && _reindexEngine != engine)
                _reindexCts?.Cancel();
        }
    }

    public Task<SearchIndexStats> GetLocalIndexStatsAsync(CancellationToken ct = default)
        => _local.GetStatsAsync(ct);

    public async Task<bool> TryStartReindexAsync(string? shareName = null, CancellationToken ct = default)
    {
        // Reindexing only makes sense against a live, selected index engine.
        var state = await ResolveStateAsync(forceFresh: true, ct);
        if (!state.Effective)
            return false;
        if (state.Engine == SearchEngine.Local && !_local.CanWrite)
            return false;

        lock (_reindexGate)
        {
            if (_reindexProgress.Running)
                return false;

            _reindexProgress = new ReindexProgress
            {
                Running = true,
                StartedAt = DateTime.UtcNow,
                ShareName = shareName
            };

            _reindexCts?.Dispose();
            _reindexCts = new CancellationTokenSource();
            _reindexEngine = state.Engine;
            var token = _reindexCts.Token;
            var engine = state.Engine;
            _reindexTask = Task.Run(() => RunReindexAsync(engine, shareName, token));
            return true;
        }
    }

    public void CancelReindex()
    {
        lock (_reindexGate)
        {
            if (_reindexProgress.Running)
                _reindexCts?.Cancel();
        }
    }

    public ReindexProgress GetReindexProgress()
    {
        lock (_reindexGate)
            return _reindexProgress;
    }

    private async Task RunReindexAsync(SearchEngine engine, string? shareName, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        try
        {
            // SYNCHRONOUS on purpose: Progress<T> posts its callbacks to the thread
            // pool, so a late "100%" callback could land AFTER the completion block
            // below and flip Running back to true — leaving the bar stuck at 100%.
            // SyncProgress applies each update inline, in order, so all reports are
            // done before ReindexAllAsync returns and the completion block wins.
            var progress = new SyncProgress<(int done, int total)>(p =>
            {
                lock (_reindexGate)
                {
                    _reindexProgress = new ReindexProgress
                    {
                        Running = true,
                        Done = p.done,
                        Total = p.total,
                        StartedAt = started,
                        ShareName = shareName
                    };
                }
            });

            if (engine == SearchEngine.Local)
            {
                bool fullPass = string.IsNullOrEmpty(shareName);
                // An index built with an older LocalIndexVersion (other extraction or path
                // rules) must re-read unchanged files too, not just touch them.
                bool rebuild = fullPass && !await _configStore.GetLocalIndexBuiltAsync();
                if (rebuild)
                    await _local.ResetContentStampsAsync(ct);
                await _local.ReindexAllAsync(progress, ct, shareName);
                // Only a completed all-shares pass makes the index complete; the indexer
                // restarts the build until this is set (canceled/failed passes never get here).
                if (fullPass)
                    await _configStore.SetLocalIndexBuiltAsync(true);
                // A rebuild rewrote most rows; give the freed space back to the disk once.
                if (rebuild)
                    await _local.CompactAsync(ct);
            }
            else
            {
                await EnsureEsInitializedAsync(ct);
                await _es.ReindexAllAsync(progress, ct, shareName);
                // Only a completed all-shares pass makes the index complete; until then every
                // start resumes the build, and the indexes of earlier mapping versions are kept.
                if (string.IsNullOrEmpty(shareName))
                {
                    await _configStore.SetElasticIndexBuiltAsync(_es.IndexName);
                    await _es.DeleteLegacyIndexesAsync(ct);
                }
            }

            lock (_reindexGate)
            {
                _reindexProgress = new ReindexProgress
                {
                    Running = false,
                    Done = _reindexProgress.Done,
                    Total = _reindexProgress.Total,
                    StartedAt = started,
                    FinishedAt = DateTime.UtcNow,
                    ShareName = shareName
                };
            }
        }
        catch (OperationCanceledException)
        {
            // User-requested stop — not an error. Keep the progress reached so far
            // and mark it as canceled for the settings page.
            lock (_reindexGate)
            {
                _reindexProgress = new ReindexProgress
                {
                    Running = false,
                    Done = _reindexProgress.Done,
                    Total = _reindexProgress.Total,
                    StartedAt = started,
                    FinishedAt = DateTime.UtcNow,
                    Canceled = true,
                    ShareName = shareName
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(LogEvents.SearchReindexFailed, ex, LogMessages.SearchReindexFailed);
            lock (_reindexGate)
            {
                _reindexProgress = new ReindexProgress
                {
                    Running = false,
                    Done = _reindexProgress.Done,
                    Total = _reindexProgress.Total,
                    StartedAt = started,
                    FinishedAt = DateTime.UtcNow,
                    Error = ex.Message,
                    ShareName = shareName
                };
            }
        }
    }

    /// <summary>
    /// An <see cref="IProgress{T}"/> that invokes its handler synchronously on the
    /// calling thread, unlike <see cref="Progress{T}"/> which posts to the thread
    /// pool. Guarantees progress updates are applied in order and before the
    /// reporting method returns — see the note at the call site.
    /// </summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
