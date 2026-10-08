using Elastic.Clients.Elasticsearch;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Search;

/// <summary>
/// The <see cref="ISearchService"/> every process actually resolves. It routes
/// both searching and index-writing to the engine selected in the settings:
///
///  • <see cref="ElasticSearchService"/> when Elasticsearch is selected AND reachable
///    (the container exists and answers a ping).
///  • <see cref="LocalIndexSearchService"/> when local indexing is selected — the index lives
///    in the application's PostgreSQL database, so there is no reachability check.
///  • <see cref="FilenameSearchService"/> otherwise — a plain filename search that
///    needs no index, so writes are simply not indexed.
///
/// This makes the system degrade gracefully: bring up the stack without the
/// Elasticsearch container and search transparently falls back to filename
/// matching; switch the engine and indexing stops/starts within a few seconds.
///
/// It also implements <see cref="ISearchAdminService"/> for the settings page
/// (engine selection + manual reindex).
/// </summary>
public sealed class SearchServiceRouter : ISearchService, ISearchAdminService
{
    // How long an effective-state probe (engine read + ES ping) is reused before we
    // re-check. Bounds DB/ping load to roughly one probe per interval per process,
    // while keeping cross-process toggles reasonably responsive.
    private static readonly TimeSpan StateTtl = TimeSpan.FromSeconds(5);

    // Upper bound for a SINGLE reachability ping so an absent container can't stall
    // a write or a search for long.
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);

    // A single failed ping is not proof that ES is down. Under Docker/WSL2 the VM
    // clock can drift and stall Elasticsearch's timer thread for ~10s at a time
    // (logs: "timer thread slept for [11s]" / "absolute clock went backwards").
    // During such a stall a probe times out even though the cluster is green. We
    // retry a couple of times before declaring the container unreachable, so a
    // transient stall doesn't flip the settings page to "offline" and block reindex.
    private const int PingAttempts = 2;

    private readonly ElasticSearchService _es;
    private readonly LocalIndexSearchService _local;
    private readonly FilenameSearchService _filename;
    private readonly ElasticsearchClient _client;
    private readonly ISearchConfigStore _configStore;
    private readonly ILogger<SearchServiceRouter> _logger;

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private SearchEngineState? _cachedState;
    private DateTime _stateExpiresUtc = DateTime.MinValue;
    private int _refreshing; // 1 while a background state probe runs
    // Edge-trigger for the "ES enabled but unreachable" warning: log once when it
    // goes down, then stay quiet until it recovers, so a persistently down cluster
    // doesn't spam a warning on every probe.
    private bool _esDownLogged;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _esInitialized;

    private readonly object _reindexGate = new();
    private ReindexProgress _reindexProgress = ReindexProgress.Idle;
    private Task? _reindexTask;
    private CancellationTokenSource? _reindexCts;
    private SearchEngine _reindexEngine;

    public SearchServiceRouter(
        ElasticSearchService es,
        LocalIndexSearchService local,
        FilenameSearchService filename,
        ElasticsearchClient client,
        ISearchConfigStore configStore,
        ILogger<SearchServiceRouter> logger)
    {
        _es = es;
        _local = local;
        _filename = filename;
        _client = client;
        _configStore = configStore;
        _logger = logger;
    }

    // ══════════════════════════════════════════
    //  Effective-state probe (cached)
    // ══════════════════════════════════════════

    private async Task<SearchEngineState> ResolveStateAsync(bool forceFresh, CancellationToken ct)
    {
        if (!forceFresh && _cachedState is { } cached && DateTime.UtcNow < _stateExpiresUtc)
            return cached;

        await _stateLock.WaitAsync(ct);
        try
        {
            if (!forceFresh && _cachedState is { } cached2 && DateTime.UtcNow < _stateExpiresUtc)
                return cached2;

            SearchEngine engine;
            try
            {
                engine = await _configStore.GetEngineAsync();
            }
            catch (Exception ex)
            {
                // If the setting can't be read, stay backward compatible (Elasticsearch) and
                // let the reachability check decide the effective state.
                _logger.LogWarning(LogEvents.SearchFlagReadFailed, ex, LogMessages.SearchFlagReadFailed);
                engine = SearchEngine.Elasticsearch;
            }

            var state = engine switch
            {
                // The local index lives in the database every process already depends on.
                SearchEngine.Local => new SearchEngineState(true, true, true, engine),
                // Still probe ES while it is off, so the settings page shows whether the
                // container would be available.
                _ => await ResolveElasticStateAsync(engine, ct)
            };

            _cachedState = state;
            _stateExpiresUtc = DateTime.UtcNow.Add(StateTtl);
            return state;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task<SearchEngineState> ResolveElasticStateAsync(SearchEngine engine, CancellationToken ct)
    {
        bool enabled = engine == SearchEngine.Elasticsearch;
        bool reachable = await PingAsync(ct);

        // Make an ES outage visible in Settings > Logging. The unreachable case
        // was previously only logged at Debug (invisible at the default Warning
        // level), so search silently fell back to filename mode with no trace.
        if (enabled && !reachable)
        {
            if (!_esDownLogged)
            {
                _logger.LogWarning(LogEvents.SearchElasticUnreachable, LogMessages.SearchElasticUnreachable);
                _esDownLogged = true;
            }
        }
        else
        {
            _esDownLogged = false;
        }

        return new SearchEngineState(enabled, reachable, enabled && reachable, engine);
    }

    private async Task<bool> PingAsync(CancellationToken ct)
    {
        for (int attempt = 1; attempt <= PingAttempts; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(PingTimeout);
                var resp = await _client.PingAsync(cts.Token);
                if (resp.IsValidResponse)
                    return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller cancelled (not our per-ping timeout) → propagate.
                throw;
            }
            catch (Exception)
            {
                // Connection refused / DNS failure / per-ping timeout (e.g. a clock
                // stall) → fall through and retry before giving up.
            }
        }

        // Every attempt failed → treat as unreachable.
        return false;
    }

    private void InvalidateState() => _stateExpiresUtc = DateTime.MinValue;

    /// <summary>The engine actually in use: the selected index engine when effective, else Filename.</summary>
    private async Task<SearchEngine> GetActiveEngineAsync(CancellationToken ct = default)
    {
        // A merely expired state is answered from cache and re-probed in the background:
        // a hanging Elasticsearch would otherwise cost the caller up to
        // PingAttempts × PingTimeout — a client search's entire time budget. An explicit
        // invalidation (admin toggle, failed search) still re-probes synchronously.
        if (_cachedState is { } known && _stateExpiresUtc != DateTime.MinValue)
        {
            if (DateTime.UtcNow >= _stateExpiresUtc && Interlocked.Exchange(ref _refreshing, 1) == 0)
                _ = RefreshStateAsync();
            return ActiveEngine(known);
        }

        return ActiveEngine(await ResolveStateAsync(forceFresh: false, ct));
    }

    private static SearchEngine ActiveEngine(SearchEngineState state)
        => state.Effective ? state.Engine : SearchEngine.Filename;

    /// <summary>The index backend currently receiving writes, or null in filename mode.</summary>
    private async Task<ISearchService?> GetActiveIndexAsync(CancellationToken ct = default)
    {
        var engine = await GetActiveEngineAsync(ct);
        return engine == SearchEngine.Filename ? null : await GetIndexAsync(engine, ct);
    }

    private async Task RefreshStateAsync()
    {
        try
        {
            await ResolveStateAsync(forceFresh: false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Keep serving the last known state; the next expiry retries.
            _logger.LogDebug(ex, "Background search-state probe failed");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>
    /// Ensures the index exists with the correct mapping before the first write or
    /// search hits Elasticsearch. Only ever called once ES is known reachable, so
    /// <see cref="ElasticSearchService.InitializeAsync"/> returns quickly.
    /// </summary>
    private async Task EnsureEsInitializedAsync(CancellationToken ct)
    {
        if (_esInitialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_esInitialized) return;
            await _es.InitializeAsync(ct);
            _esInitialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ══════════════════════════════════════════
    //  ISearchService — indexing hooks
    // ══════════════════════════════════════════

    public async Task onFileCreated(string absolutePath, Task<Stream> fileData, CancellationToken ct = default)
    {
        if (await GetActiveIndexAsync(ct) is { } index)
        {
            await index.onFileCreated(absolutePath, fileData, ct);
            return;
        }

        // Not indexing: the caller eagerly opened a read stream for us — release it
        // so we don't leak an OS file handle (which on Windows bind mounts blocks
        // later renames/moves of the file).
        await DrainAsync(fileData);
    }

    public async Task onFileDeleted(string absolutePath)
    {
        if (await GetActiveIndexAsync() is { } index)
            await index.onFileDeleted(absolutePath);
    }

    public async Task onDirectoryCreated(string absolutePath)
    {
        if (await GetActiveIndexAsync() is { } index)
            await index.onDirectoryCreated(absolutePath);
    }

    public async Task onDirectoryDeleted(string absolutePath)
    {
        if (await GetActiveIndexAsync() is { } index)
            await index.onDirectoryDeleted(absolutePath);
    }

    public async Task onFileRenamed(string oldAbsolutePath, string newAbsolutePath)
    {
        if (await GetActiveIndexAsync() is { } index)
            await index.onFileRenamed(oldAbsolutePath, newAbsolutePath);
    }

    public async Task onDirectoryRenamed(string oldAbsolutePath, string newAbsolutePath)
    {
        if (await GetActiveIndexAsync() is { } index)
            await index.onDirectoryRenamed(oldAbsolutePath, newAbsolutePath);
    }

    private static async Task DrainAsync(Task<Stream> fileData)
    {
        try
        {
            var stream = await fileData;
            await stream.DisposeAsync();
        }
        catch
        {
            // Nothing to release / already faulted — indexing was skipped anyway.
        }
    }

    // ══════════════════════════════════════════
    //  ISearchService — search
    // ══════════════════════════════════════════

    public async Task<List<FileDocument>> SearchAsync(
        string searchText, UserContext user,
        string? shareName = null, string? pathPrefix = null,
        CancellationToken ct = default, bool includeRecycleBin = false)
    {
        // Searching inside a recycle bin always shows its contents, whatever the toggle says.
        includeRecycleBin |= IsRecycleBinScope(pathPrefix);

        var engine = await GetActiveEngineAsync(ct);
        if (engine != SearchEngine.Filename)
        {
            try
            {
                var index = await GetIndexAsync(engine, ct);
                return await index.SearchAsync(searchText, user, shareName, pathPrefix, ct, includeRecycleBin);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // The transport may wrap a cancellation in its own exception type. That is
                // the caller's timeout, not an index failure: no fallback, no warning.
                throw new OperationCanceledException(ct);
            }
            catch (Exception ex)
            {
                if (engine == SearchEngine.Local)
                    _logger.LogWarning(LogEvents.SearchLocalFailed, ex, LogMessages.SearchLocalFailed);
                else
                    _logger.LogWarning(LogEvents.SearchElasticFailed, ex, LogMessages.SearchElasticFailed);
                InvalidateState();
            }
        }

        return await _filename.SearchAsync(searchText, user, shareName, pathPrefix, ct, includeRecycleBin);
    }

    /// <summary>
    /// Whether a scope folder lies in a recycle bin. Any ".RECYCLE_BIN" segment counts: only the
    /// share's/home's own bin is indexed (dot folders elsewhere are excluded), so no depth is needed.
    /// </summary>
    internal static bool IsRecycleBinScope(string? pathPrefix)
        => (pathPrefix ?? string.Empty).Replace('\\', '/').Split('/')
            .Any(s => s.Equals(ShareEntryPolicy.RecycleBinName, StringComparison.OrdinalIgnoreCase));

    private async Task<ISearchService> GetIndexAsync(SearchEngine engine, CancellationToken ct)
    {
        if (engine == SearchEngine.Local)
            return _local;
        await EnsureEsInitializedAsync(ct);
        return _es;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var state = await ResolveStateAsync(forceFresh: true, ct);

        // The local index schema is created by migrations; only ES needs an index bootstrap.
        if (state.Engine == SearchEngine.Local)
            return;

        if (!state.Reachable)
        {
            _logger.LogDebug(LogEvents.SearchElasticUnreachable, LogMessages.SearchElasticUnreachable);
            return;
        }

        if (!state.Enabled)
        {
            _logger.LogDebug(LogEvents.SearchElasticDisabled, LogMessages.SearchElasticDisabled);
            return;
        }

        await EnsureEsInitializedAsync(ct);
    }

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
