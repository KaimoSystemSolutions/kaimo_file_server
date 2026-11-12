namespace Kaimo_File_Server.Core.Services.Search;

/// <summary>
/// Administrative surface for the search engine, used by the settings page:
/// select the engine (Elasticsearch, local index, filename) and trigger a manual full reindex. Implemented by
/// the search router so the UI never has to know which backend is active.
/// </summary>
public interface ISearchAdminService
{
    /// <summary>Reads the desired flag, pings Elasticsearch and reports the effective state.</summary>
    Task<SearchEngineState> GetStateAsync(CancellationToken ct = default);

    /// <summary>Persists the desired on/off flag for Elasticsearch.</summary>
    Task SetElasticEnabledAsync(bool enabled, CancellationToken ct = default);

    /// <summary>
    /// Persists the selected engine. Switching away from the engine a reindex runs for
    /// cancels that reindex.
    /// </summary>
    Task SetEngineAsync(SearchEngine engine, CancellationToken ct = default);

    /// <summary>Document count and on-disk size of the local index.</summary>
    Task<SearchIndexStats> GetLocalIndexStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Kicks off a reindex in the background. When <paramref name="shareName"/> is
    /// null or empty, every enabled share is reindexed; otherwise only that share.
    /// Returns false if no index-backed engine is currently active (disabled or
    /// unreachable), indexing is blocked (read-only demo) or a reindex is already running.
    /// </summary>
    Task<bool> TryStartReindexAsync(string? shareName = null, CancellationToken ct = default);

    /// <summary>
    /// Requests cancellation of the currently running reindex, if any. Returns
    /// immediately; the run stops at its next file/directory boundary and reports
    /// its progress as <see cref="ReindexProgress.Canceled"/>.
    /// </summary>
    void CancelReindex();

    /// <summary>Current/last reindex progress (in-memory, process-local).</summary>
    ReindexProgress GetReindexProgress();
}
