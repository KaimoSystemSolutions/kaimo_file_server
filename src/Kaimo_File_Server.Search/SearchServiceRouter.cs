using Elastic.Clients.Elasticsearch;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Search;

/// <summary>
/// The <see cref="ISearchService"/> every process actually resolves. It routes
/// both searching and index-writing to one of two backends:
///
///  • <see cref="ElasticSearchService"/> when Elasticsearch is enabled (admin flag)
///    AND reachable (the container exists and answers a ping).
///  • <see cref="FilenameSearchService"/> otherwise — a plain filename search that
///    needs no index, so writes are simply not indexed.
///
/// This makes the system degrade gracefully: bring up the stack without the
/// Elasticsearch container and search transparently falls back to filename
/// matching; flip the admin toggle and indexing stops/starts within a few seconds.
///
/// It also implements <see cref="ISearchAdminService"/> for the settings page
/// (toggle + manual reindex).
/// </summary>
public sealed class SearchServiceRouter : ISearchService, ISearchAdminService
{
    // How long an effective-state probe (flag read + ES ping) is reused before we
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
    private readonly FilenameSearchService _filename;
    private readonly ElasticsearchClient _client;
    private readonly ISearchConfigStore _configStore;
    private readonly ILogger<SearchServiceRouter> _logger;

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private SearchEngineState? _cachedState;
    private DateTime _stateExpiresUtc = DateTime.MinValue;
    // Edge-trigger for the "ES enabled but unreachable" warning: log once when it
    // goes down, then stay quiet until it recovers, so a persistently down cluster
    // doesn't spam a warning on every probe.
    private bool _esDownLogged;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _esInitialized;

    private readonly object _reindexGate = new();
    private ReindexProgress _reindexProgress = ReindexProgress.Idle;
    private Task? _reindexTask;

    public SearchServiceRouter(
        ElasticSearchService es,
        FilenameSearchService filename,
        ElasticsearchClient client,
        ISearchConfigStore configStore,
        ILogger<SearchServiceRouter> logger)
    {
        _es = es;
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

            bool enabled;
            try
            {
                enabled = await _configStore.GetElasticEnabledAsync(fallback: true);
            }
            catch (Exception ex)
            {
                // If the flag can't be read, stay backward compatible (enabled) and
                // let the reachability check decide the effective state.
                _logger.LogWarning(LogEvents.SearchFlagReadFailed, ex, LogMessages.SearchFlagReadFailed);
                enabled = true;
            }

            bool reachable = await PingAsync(ct);

            var state = new SearchEngineState(enabled, reachable, enabled && reachable);

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

            _cachedState = state;
            _stateExpiresUtc = DateTime.UtcNow.Add(StateTtl);
            return state;
        }
        finally
        {
            _stateLock.Release();
        }
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

    private async Task<bool> IsElasticActiveAsync(CancellationToken ct = default)
        => (await ResolveStateAsync(forceFresh: false, ct)).Effective;

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
        if (await IsElasticActiveAsync(ct))
        {
            await EnsureEsInitializedAsync(ct);
            await _es.onFileCreated(absolutePath, fileData, ct);
            return;
        }

        // Not indexing: the caller eagerly opened a read stream for us — release it
        // so we don't leak an OS file handle (which on Windows bind mounts blocks
        // later renames/moves of the file).
        await DrainAsync(fileData);
    }

    public async Task onFileDeleted(string absolutePath)
    {
        if (await IsElasticActiveAsync())
            await _es.onFileDeleted(absolutePath);
    }

    public async Task onDirectoryCreated(string absolutePath)
    {
        if (await IsElasticActiveAsync())
            await _es.onDirectoryCreated(absolutePath);
    }

    public async Task onDirectoryDeleted(string absolutePath)
    {
        if (await IsElasticActiveAsync())
            await _es.onDirectoryDeleted(absolutePath);
    }

    public async Task onFileRenamed(string oldAbsolutePath, string newAbsolutePath)
    {
        if (await IsElasticActiveAsync())
            await _es.onFileRenamed(oldAbsolutePath, newAbsolutePath);
    }

    public async Task onDirectoryRenamed(string oldAbsolutePath, string newAbsolutePath)
    {
        if (await IsElasticActiveAsync())
            await _es.onDirectoryRenamed(oldAbsolutePath, newAbsolutePath);
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
        CancellationToken ct = default)
    {
        if (await IsElasticActiveAsync(ct))
        {
            try
            {
                await EnsureEsInitializedAsync(ct);
                return await _es.SearchAsync(searchText, user, shareName, pathPrefix, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(LogEvents.SearchElasticFailed, ex, LogMessages.SearchElasticFailed);
                InvalidateState();
            }
        }

        return await _filename.SearchAsync(searchText, user, shareName, pathPrefix, ct);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var state = await ResolveStateAsync(forceFresh: true, ct);

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

    public async Task SetElasticEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        await _configStore.SetElasticEnabledAsync(enabled);
        InvalidateState();
    }

    public async Task<bool> TryStartReindexAsync(string? shareName = null, CancellationToken ct = default)
    {
        // Reindexing only makes sense against a live, enabled Elasticsearch.
        if (!(await ResolveStateAsync(forceFresh: true, ct)).Effective)
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

            _reindexTask = Task.Run(() => RunReindexAsync(shareName));
            return true;
        }
    }

    public ReindexProgress GetReindexProgress()
    {
        lock (_reindexGate)
            return _reindexProgress;
    }

    private async Task RunReindexAsync(string? shareName = null)
    {
        var started = DateTime.UtcNow;
        try
        {
            await EnsureEsInitializedAsync(CancellationToken.None);

            var progress = new Progress<(int done, int total)>(p =>
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

            await _es.ReindexAllAsync(progress, CancellationToken.None, shareName);

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
}
