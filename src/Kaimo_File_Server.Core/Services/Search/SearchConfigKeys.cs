namespace Kaimo_File_Server.Core.Services.Search;

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
    /// The selected <see cref="SearchEngine"/> (stored by name). Supersedes
    /// <see cref="ElasticEnabledKey"/>: while unset, the legacy flag decides
    /// (true → Elasticsearch, false → Filename), so existing installations keep their behavior.
    /// </summary>
    public const string EngineKey = "search.engine";

    /// <summary>
    /// The highest change-log <c>Seq</c> the background indexer has already applied to
    /// the Elasticsearch index. Persisted so the indexer resumes where it left off across restarts.
    /// </summary>
    public const string IndexCursorKey = "search.index.cursor";

    /// <summary>Change-log cursor of the local (PostgreSQL) index, kept apart from the ES one.</summary>
    public const string LocalIndexCursorKey = "search.local.cursor";

    /// <summary>
    /// The <see cref="LocalIndexVersion"/> the last completed full (all-shares) build of the
    /// local index ran with. While it differs, the indexer (re)starts a full build that
    /// re-extracts every file, so an interrupted first build is never left half-done and a
    /// changed indexing rule reaches documents of unchanged files.
    /// </summary>
    public const string LocalIndexBuiltKey = "search.local.built-version";

    /// <summary>
    /// Name of the Elasticsearch index whose last full (all-shares) build completed. While it
    /// differs from the index in use, the router (re)starts a full build on startup, so an
    /// interrupted first build of a new index is never left half-done.
    /// </summary>
    public const string ElasticIndexBuiltKey = "search.es.built-index";

    /// <summary>
    /// Version of what the local index stores per file. Bump it whenever content extraction
    /// or the set of indexed paths changes. 2: encoded blobs stripped, recycle bins indexed.
    /// 3: base64url payloads stripped as well. 4: UTF-16/Windows-1252 decoded, control characters
    /// tolerated, text NFC-normalized, hashes kept by the blob filter, symbolic links skipped.
    /// 5: word vector (tsvector) and word dictionary replace the content trigram index.
    /// </summary>
    public const int LocalIndexVersion = 5;

    /// <summary>The cursor key of an index-backed engine.</summary>
    public static string CursorKeyFor(SearchEngine engine)
        => engine == SearchEngine.Local ? LocalIndexCursorKey : IndexCursorKey;

    /// <summary>
    /// Resolves the effective engine from the stored values. An unknown or missing engine name
    /// falls back to the legacy Elasticsearch flag.
    /// </summary>
    public static SearchEngine ParseEngine(string? storedEngine, bool legacyElasticEnabled)
        => Enum.TryParse<SearchEngine>(storedEngine, ignoreCase: true, out var engine)
           && Enum.IsDefined(engine)
            ? engine
            : legacyElasticEnabled ? SearchEngine.Elasticsearch : SearchEngine.Filename;
}
