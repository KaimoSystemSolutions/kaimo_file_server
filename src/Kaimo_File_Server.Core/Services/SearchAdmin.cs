namespace Kaimo_File_Server.Search;

/// <summary>
/// Central conventions for the config keys that control the search engine.
/// Lives in Core (like <see cref="ISearchService"/>) so both the Search backend
/// and the Infrastructure config adapter can reference it without a cycle.
/// </summary>
public static class SearchConfigKeys
{
    /// <summary>
    /// Bool flag: whether Elasticsearch should be used when it is reachable.
    /// Default <c>true</c> → backward compatible (ES is used as before, until an
    /// admin turns it off). When <c>false</c>, the filename fallback is used and
    /// nothing is written to the index.
    /// </summary>
    public const string ElasticEnabledKey = "search.elasticsearch.enabled";

    /// <summary>
    /// The highest change-log <c>Seq</c> the background indexer has already applied to
    /// the index. Persisted so the indexer resumes where it left off across restarts.
    /// </summary>
    public const string IndexCursorKey = "search.index.cursor";
}

/// <summary>
/// Cross-process access to the search engine's desired-state flag. Implemented in
/// Infrastructure over <c>IConfigRepository</c>; kept as an abstraction here so the
/// Search assembly never has to depend on Infrastructure/EF.
/// </summary>
public interface ISearchConfigStore
{
    /// <summary>Reads the "use Elasticsearch" flag fresh (cross-process safe).</summary>
    Task<bool> GetElasticEnabledAsync(bool fallback = true);

    /// <summary>Persists the "use Elasticsearch" flag.</summary>
    Task SetElasticEnabledAsync(bool enabled);

    /// <summary>Reads the background indexer's persisted change-log cursor (0 when unset).</summary>
    Task<long> GetIndexCursorAsync();

    /// <summary>Persists the background indexer's change-log cursor.</summary>
    Task SetIndexCursorAsync(long seq);
}

/// <summary>Effective runtime state of the search engine, for the settings UI.</summary>
public sealed record SearchEngineState(
    bool Enabled,
    bool Reachable,
    bool Effective);

/// <summary>Progress of a running (or last) full reindex pass.</summary>
public sealed class ReindexProgress
{
    public bool Running { get; init; }
    public int Total { get; init; }
    public int Done { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public string? Error { get; init; }

    /// <summary>True when the run was stopped by the user (not an error, not a full pass).</summary>
    public bool Canceled { get; init; }

    /// <summary>Share this run is scoped to; null/empty means all shares.</summary>
    public string? ShareName { get; init; }

    public int Percent => Total > 0 ? (int)(100L * Done / Total) : 0;

    public static ReindexProgress Idle => new();
}

/// <summary>
/// Administrative surface for the search engine, used by the settings page:
/// toggle Elasticsearch on/off and trigger a manual full reindex. Implemented by
/// the search router so the UI never has to know which backend is active.
/// </summary>
public interface ISearchAdminService
{
    /// <summary>Reads the desired flag, pings Elasticsearch and reports the effective state.</summary>
    Task<SearchEngineState> GetStateAsync(CancellationToken ct = default);

    /// <summary>Persists the desired on/off flag for Elasticsearch.</summary>
    Task SetElasticEnabledAsync(bool enabled, CancellationToken ct = default);

    /// <summary>
    /// Kicks off a reindex in the background. When <paramref name="shareName"/> is
    /// null or empty, every enabled share is reindexed; otherwise only that share.
    /// Returns false if Elasticsearch is not currently active (disabled or
    /// unreachable) or a reindex is already running.
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
