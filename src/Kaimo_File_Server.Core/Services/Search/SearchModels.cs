namespace Kaimo_File_Server.Core.Services.Search;

/// <summary>
/// The search engine gave no answer at all (connection refused, timeout) — as opposed to
/// rejecting a single document. Index writes throw this so the change-log indexer stops and
/// retries the entry once the engine is back, instead of dropping the change.
/// </summary>
public sealed class SearchUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>The search backend selected in the settings. Exactly one is active.</summary>
public enum SearchEngine
{
    /// <summary>No index: plain file-name walk over the shares.</summary>
    Filename,

    /// <summary>Full-text index in the Elasticsearch container.</summary>
    Elasticsearch,

    /// <summary>Full-text index in the application's PostgreSQL database.</summary>
    Local
}

/// <summary>Effective runtime state of the search engine, for the settings UI.</summary>
/// <param name="Enabled">Whether an index-backed engine (ES or Local) is selected.</param>
/// <param name="Reachable">Whether the selected engine answers; always true for Local and Filename.</param>
/// <param name="Effective">True when the selected index-backed engine is actually in use.</param>
/// <param name="Engine">The selected engine.</param>
public sealed record SearchEngineState(
    bool Enabled,
    bool Reachable,
    bool Effective,
    SearchEngine Engine = SearchEngine.Elasticsearch);

/// <summary>Size of the local search index, for the settings UI.</summary>
public sealed record SearchIndexStats(long Documents, long Bytes);

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
