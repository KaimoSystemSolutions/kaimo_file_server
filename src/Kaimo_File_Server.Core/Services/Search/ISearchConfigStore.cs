namespace Kaimo_File_Server.Core.Services.Search;

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

    /// <summary>Reads the selected engine fresh (cross-process safe), honoring the legacy flag.</summary>
    Task<SearchEngine> GetEngineAsync();

    /// <summary>Persists the selected engine.</summary>
    Task SetEngineAsync(SearchEngine engine);

    /// <summary>
    /// Whether any search key was ever stored (engine, legacy Elasticsearch flag, an index cursor
    /// or a build marker). False only on an installation whose search never ran or was configured.
    /// </summary>
    Task<bool> HasSearchStateAsync();

    /// <summary>Reads the change-log cursor of an index-backed engine (0 when unset).</summary>
    Task<long> GetIndexCursorAsync(SearchEngine engine);

    /// <summary>Persists the change-log cursor of an index-backed engine.</summary>
    Task SetIndexCursorAsync(SearchEngine engine, long seq);

    /// <summary>
    /// Whether a full build of the local index has completed with the current
    /// <see cref="SearchConfigKeys.LocalIndexVersion"/> (false when unset or older).
    /// </summary>
    Task<bool> GetLocalIndexBuiltAsync();

    /// <summary>Records that a full build has completed with the current version (or clears it).</summary>
    Task SetLocalIndexBuiltAsync(bool built);

    /// <summary>
    /// Name of the Elasticsearch index whose last full build completed ("" when none did).
    /// </summary>
    Task<string> GetElasticIndexBuiltAsync();

    /// <summary>Records the index whose full build completed ("" clears it).</summary>
    Task SetElasticIndexBuiltAsync(string indexName);
}
