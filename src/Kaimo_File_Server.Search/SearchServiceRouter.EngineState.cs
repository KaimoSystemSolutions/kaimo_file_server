using Kaimo_File_Server.Core.Logging;

namespace Kaimo_File_Server.Search;

public partial class SearchServiceRouter
{
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
        bool needsBuild;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_esInitialized) return;
            bool created = await _es.EnsureIndexAsync(ct);
            needsBuild = await NeedsFullEsBuildAsync(created);
            _esInitialized = true;
        }
        finally
        {
            _initLock.Release();
        }

        // A new index (first activation, a new mapping version, a lost volume) is empty, and one
        // whose first build was interrupted is incomplete: fill it from disk. A reindex already
        // running (it may have created the index itself) is left alone.
        if (needsBuild)
            await TryStartReindexAsync(shareName: null, CancellationToken.None);
    }

    /// <summary>
    /// True unless a full pass has completed into the index in use. A freshly created index
    /// clears the record first, since the one it replaces may have been built before.
    /// </summary>
    private async Task<bool> NeedsFullEsBuildAsync(bool created)
    {
        try
        {
            if (created)
                await _configStore.SetElasticIndexBuiltAsync(string.Empty);
            return created || await _configStore.GetElasticIndexBuiltAsync() != _es.IndexName;
        }
        catch (Exception ex)
        {
            // The record lives in the database; without it, only a newly created index is known
            // to need a build. Searching must not fail over this.
            _logger.LogWarning(ex, "Could not read whether the search index was completely built");
            return created;
        }
    }
}
