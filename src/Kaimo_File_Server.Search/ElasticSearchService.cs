using Elastic.Clients.Elasticsearch;
using Elastic.Transport;

namespace Kaimo_File_Server.Search;

public partial class ElasticSearchService : ISearchService
{
    // NOTE: the index name is versioned. Changing a field's analyzer requires a
    // NEW index (analyzers are fixed at field-creation time). Bumping the suffix
    // makes EnsureIndexAsync build a fresh index with the current mapping; the router
    // then starts a full reindex and deletes the legacy indexes below once it has completed.
    // v3: query tokenizer splits on punctuation, query terms truncated to the n-gram span,
    // CJK via whole-character sub-fields, ß folded to ss, content n-grams 3–10.
    internal const string DefaultIndexName = "kaimo-files-v3";
    private static readonly string[] LegacyIndexNames = ["kaimo-files-v2", "kaimo-files"];

    private readonly string _indexName;

    /// <summary>The index this service reads and writes.</summary>
    internal string IndexName => _indexName;

    // Index-time analyzer of file names: breaks tokens into n-grams so a substring of a word
    // (e.g. "ocum" inside "document") still produces a matching token.
    private const string NgramIndexAnalyzer = "kaimo_ngram_index";
    // Search-time analyzer: splits the user's query exactly where the index tokenizer splits
    // (anything but letters and digits: ".", "_", ":" …), lowercases and cuts each term to the
    // longest indexed n-gram, so "invoice.pdf" and words longer than the span still match.
    private const string NgramSearchAnalyzer = "kaimo_ngram_search";
    // Content uses a narrower span: 2–20 grams cost ~19 terms per character and made the index
    // ~35x the size of the local one; 3–10 cost 8. Whole words are matched via "content.std".
    private const string ContentIndexAnalyzer = "kaimo_content_index";
    private const string ContentSearchAnalyzer = "kaimo_content_search";
    // Whole-word analysis (standard tokenizer: one token per CJK character) for ranking, fuzzy
    // matching and scripts without spaces, where single characters are words.
    private const string WordAnalyzer = "kaimo_words";

    // Flat score of any file-name match. BM25 content scores stay in the low hundreds even for
    // long queries, so this keeps name hits first while float precision still resolves the ties.
    private const float NameTierBoost = 10_000;

    private const int NgramMinGram = 2;
    private const int NgramMaxGram = 20;
    private const int ContentMinGram = 3;
    private const int ContentMaxGram = 10;

    // Documents read per page while sweeping stale ones after a reindex.
    private const int SweepPageSize = 1000;

    // The 2–20 n-gram analysis holds every distinct gram of a document in heap at once. High-
    // entropy content (base64 the blob filter misses, random identifiers) OOM-kills a 512 MB node
    // at ~400 K chars and even a 1 GB node at 1 M chars; 200 K chars survived 512 MB in tests.
    // ponytail: fixed cap, content beyond it is not searchable; lower NgramMaxGram (needs a new
    // index version) if deeper full-text search on large files is required.
    private const int MaxContentChars = 200_000;

    // Page size for pulling results from ES before ACL filtering; further pages are
    // fetched while too few readable hits remain (see SearchLimits).
    private const int RawFetchSize = 50;

    private readonly ElasticsearchClient _client;
    private readonly ILogger<ElasticSearchService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SearchAclFilter _aclFilter;

    public ElasticSearchService(
        ElasticsearchClient client,
        ILogger<ElasticSearchService> logger,
        IServiceScopeFactory scopeFactory,
        SearchAclFilter aclFilter)
        : this(client, logger, scopeFactory, aclFilter, DefaultIndexName)
    {
    }

    /// <summary>Uses <paramref name="indexName"/> instead of the production index (tests).</summary>
    internal ElasticSearchService(
        ElasticsearchClient client,
        ILogger<ElasticSearchService> logger,
        IServiceScopeFactory scopeFactory,
        SearchAclFilter aclFilter,
        string indexName)
    {
        _client = client;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _aclFilter = aclFilter;
        _indexName = indexName;
    }

    public async Task onFileCreated(string absolutePath, Task<Stream> fileData, CancellationToken ct = default)
    {
        try
        {
            await IndexDocumentIfNotExistsAsync(absolutePath, fileData, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted/renamed before the indexer got to it; the following log entry handles it.
            _logger.LogDebug("File vanished before indexing: {Path}", absolutePath);
        }
        catch (Exception ex) when (ex is not SearchUnavailableException)
        {
            _logger.LogError(ex, "Indexing failed for {Path}", absolutePath);
        }
    }

    /// <summary>
    /// Throws <see cref="SearchUnavailableException"/> when Elasticsearch never answered
    /// (connection refused, timeout) or answered that it cannot serve the request yet (502/503/504,
    /// e.g. shards still unassigned right after a restart while the ping already succeeds), so the
    /// caller can retry instead of losing the change. Any other HTTP error (e.g. a 4xx document
    /// rejection, or a 429 circuit breaker on a huge document) is returned for the caller to handle.
    /// </summary>
    private static T Answered<T>(T response) where T : TransportResponse
    {
        if (response.ApiCallDetails is { HttpStatusCode: null or 502 or 503 or 504 } call)
            throw new SearchUnavailableException(
                call.HttpStatusCode is { } status
                    ? $"Elasticsearch is unavailable (HTTP {status})"
                    : "Elasticsearch did not respond: " + (call.OriginalException?.Message ?? "no HTTP response"),
                call.OriginalException);
        return response;
    }

    public async Task onFileDeleted(string absolutePath)
    {
        try
        {
            // Delete by id (id == hash(absolutePath)), not by query: delete-by-query only sees
            // refreshed segments, so a file indexed <1s earlier was missed and left orphaned.
            var response = Answered(await _client.DeleteAsync<FileDocument>(
                SearchDocuments.GetStableId(absolutePath), d => d.Index(_indexName)));

            if (response.Result == Result.NotFound)
                // Never indexed (hidden path, pre-existing file, vanished before indexing) — fine.
                _logger.LogDebug("No document found for path '{Path}'", absolutePath);
            else if (!response.IsValidResponse)
                _logger.LogError("Deletion failed for {Path}: {Error}",
                    absolutePath, response.DebugInformation);
            else
                _logger.LogDebug("Removed document for '{Path}' from the index", absolutePath);
        }
        catch (Exception ex) when (ex is not SearchUnavailableException)
        {
            _logger.LogError(ex, "Deletion failed for {Path}", absolutePath);
        }
    }

    public Task onDirectoryCreated(string absolutePath)
    {
        return Task.Run(async () =>
        {
            try
            {
                var share = await SearchDocuments.ResolveShareAsync(_scopeFactory, absolutePath);
                var doc = share is null
                    ? null
                    : SearchDocuments.BuildDirectoryDocument(absolutePath, share);
                if (doc is null)
                {
                    // Share-root directory (or a path outside every share) — not a
                    // searchable folder within a share, so nothing to index.
                    _logger.LogDebug("Directory create (not indexed): '{Path}'", absolutePath);
                    return;
                }

                await IndexDirectoryDocumentAsync(doc);
            }
            catch (Exception ex) when (ex is not SearchUnavailableException)
            {
                _logger.LogError(ex, "Directory indexing failed for {Path}", absolutePath);
            }
        });
    }

    /// <summary>
    /// Indexes a directory document if it is not already present. A folder has no
    /// content that could change, so an existing entry is left untouched (renames
    /// are handled separately via <see cref="onDirectoryRenamed"/>).
    /// </summary>
    private async Task IndexDirectoryDocumentAsync(FileDocument doc, CancellationToken ct = default)
    {
        var existing = Answered(await _client.GetAsync<FileDocument>(doc.Id, g => g.Index(_indexName), ct));
        if (existing.Found)
        {
            _logger.LogDebug("Directory {Id} already indexed, skipping", doc.Id);
            return;
        }

        var response = Answered(await _client.IndexAsync(doc, idx => idx
            .Index(_indexName)
            .Id(doc.Id), ct));

        if (!response.IsValidResponse)
            _logger.LogError("Directory indexing failed for {Id}: {Error}",
                doc.Id, response.DebugInformation);
    }

    public async Task onDirectoryDeleted(string absolutePath)
    {
        await Task.Run(async () =>
        {
            try
            {
                string prefix = absolutePath.TrimEnd(Path.DirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;

                // delete-by-query only sees refreshed segments; refresh first so children
                // indexed moments ago are not left orphaned.
                Answered(await _client.Indices.RefreshAsync(_indexName));

                var response = Answered(await _client.DeleteByQueryAsync<FileDocument>(_indexName, d => d
                    .Query(q => q
                        .Bool(b => b
                            .Should(
                                s => s.Term(t => t.Field("absolutePath").Value(absolutePath)),
                                s => s.Prefix(p => p.Field("absolutePath").Value(prefix))
                            )
                            .MinimumShouldMatch(1)
                        )
                    )
                ));

                if (!response.IsValidResponse)
                    _logger.LogError("DeleteByQuery failed for directory '{Path}': {Error}",
                        absolutePath, response.DebugInformation);
                else
                    _logger.LogDebug(
                        "Removed {Count} document(s) of directory '{Path}' from the index",
                        response.Deleted, absolutePath);
            }
            catch (Exception ex) when (ex is not SearchUnavailableException)
            {
                _logger.LogError(ex, "Deleting the directory failed for {Path}", absolutePath);
            }
        });
    }

}
