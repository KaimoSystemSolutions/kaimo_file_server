using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Analysis;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;

namespace Kaimo_File_Server.Search;

public partial class ElasticSearchService
{
    // ══════════════════════════════════════════
    //  Index lifecycle
    // ══════════════════════════════════════════

    public Task InitializeAsync(CancellationToken ct = default) => EnsureIndexAsync(ct);

    /// <summary>
    /// Creates the index with the current mapping unless it exists. Returns true when it was
    /// created now: it is empty then, and the caller starts a full reindex.
    /// </summary>
    public async Task<bool> EnsureIndexAsync(CancellationToken ct = default)
    {
        // Don't touch the index until the cluster can actually allocate shards.
        // Creating an index against an unhealthy (red) node leaves its primary
        // shard unassigned -> every later _search returns 503 "all shards failed".
        await WaitForClusterReadyAsync(ct);

        var exists = await _client.Indices.ExistsAsync(_indexName, ct);
        if (exists.Exists)
            return false;

        TokenChar[] wordChars = [TokenChar.Letter, TokenChar.Digit];
        string[] fold = ["kaimo_fold"];
        var createRequest = new CreateIndexRequest(_indexName)
        {
            Settings = new IndexSettings
            {
                // Single-node dev cluster: one shard, no replicas. A replica on a
                // single node can never be allocated and would keep the index in a
                // permanent "yellow" state.
                NumberOfShards = 1,
                NumberOfReplicas = 0,
                // Required because our n-gram span (max - min) is greater than the
                // default allowed difference of 1.
                MaxNgramDiff = NgramMaxGram - NgramMinGram,
                Analysis = new IndexSettingsAnalysis
                {
                    // "STRASSE" and "straße" are the same word; lowercase alone keeps them apart.
                    CharFilters = new CharFilters
                    {
                        { "kaimo_fold", new MappingCharFilter { Mappings = ["ß => ss", "ẞ => ss"] } }
                    },
                    Tokenizers = new Tokenizers
                    {
                        {
                            "kaimo_ngram_tokenizer", new NGramTokenizer
                            {
                                MinGram = NgramMinGram,
                                MaxGram = NgramMaxGram,
                                TokenChars = wordChars
                            }
                        },
                        {
                            "kaimo_content_tokenizer", new NGramTokenizer
                            {
                                MinGram = ContentMinGram,
                                MaxGram = ContentMaxGram,
                                TokenChars = wordChars
                            }
                        },
                        // Splits exactly where the n-gram tokenizers split (Java regex).
                        { "kaimo_query_tokenizer", new PatternTokenizer { Pattern = @"[^\p{L}\p{N}]+" } }
                    },
                    TokenFilters = new TokenFilters
                    {
                        { "kaimo_name_truncate", new TruncateTokenFilter { Length = NgramMaxGram } },
                        { "kaimo_content_truncate", new TruncateTokenFilter { Length = ContentMaxGram } }
                    },
                    Analyzers = new Analyzers
                    {
                        {
                            NgramIndexAnalyzer, new CustomAnalyzer
                            {
                                CharFilter = fold,
                                Tokenizer = "kaimo_ngram_tokenizer",
                                Filter = ["lowercase"]
                            }
                        },
                        {
                            NgramSearchAnalyzer, new CustomAnalyzer
                            {
                                CharFilter = fold,
                                Tokenizer = "kaimo_query_tokenizer",
                                Filter = ["lowercase", "kaimo_name_truncate"]
                            }
                        },
                        {
                            ContentIndexAnalyzer, new CustomAnalyzer
                            {
                                CharFilter = fold,
                                Tokenizer = "kaimo_content_tokenizer",
                                Filter = ["lowercase"]
                            }
                        },
                        {
                            ContentSearchAnalyzer, new CustomAnalyzer
                            {
                                CharFilter = fold,
                                Tokenizer = "kaimo_query_tokenizer",
                                Filter = ["lowercase", "kaimo_content_truncate"]
                            }
                        },
                        {
                            WordAnalyzer, new CustomAnalyzer
                            {
                                CharFilter = fold,
                                Tokenizer = "standard",
                                Filter = ["lowercase"]
                            }
                        }
                    }
                }
            },
            Mappings = new TypeMapping
            {
                Properties = getProperties()
            }
        };

        var response = await _client.Indices.CreateAsync(createRequest, ct);

        if (!response.IsValidResponse)
        {
            _logger.LogError("Index could not be created: {Error}", response.DebugInformation);
            return false;
        }

        _logger.LogInformation("Search index '{Index}' created; a full reindex fills it", _indexName);
        return true;
    }

    /// <summary>
    /// Frees the disk space of indexes built with an earlier mapping version (best effort). Called
    /// once the production index is completely built, so an interrupted build loses nothing.
    /// </summary>
    internal async Task DeleteLegacyIndexesAsync(CancellationToken ct)
    {
        if (_indexName != DefaultIndexName)
            return;

        foreach (var legacy in LegacyIndexNames)
        {
            try
            {
                if ((await _client.Indices.ExistsAsync(legacy, ct)).Exists
                    && (await _client.Indices.DeleteAsync(legacy, ct)).IsValidResponse)
                    _logger.LogInformation("Deleted the outdated search index '{Index}'", legacy);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not delete the outdated search index '{Index}'", legacy);
            }
        }
    }

    /// <summary>
    /// Polls the cluster health until it is at least YELLOW (shards allocatable)
    /// or a timeout is reached. Prevents creating/using the index while the node
    /// is RED — a state in which the primary shard never allocates and all
    /// searches fail with 503 "all shards failed".
    /// </summary>
    private async Task WaitForClusterReadyAsync(CancellationToken ct)
    {
        const int maxAttempts = 30;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var health = await _client.Cluster.HealthAsync(ct);
                if (health.IsValidResponse && health.Status != HealthStatus.Red)
                {
                    if (attempt > 1)
                        _logger.LogDebug("Elasticsearch cluster ready (status {Status})", health.Status);
                    return;
                }

                _logger.LogWarning(
                    "Elasticsearch cluster not ready yet (status red), attempt {Attempt}/{Max}",
                    attempt, maxAttempts);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Elasticsearch not reachable yet, attempt {Attempt}/{Max}", attempt, maxAttempts);
            }

            await Task.Delay(2000, ct);
        }

        _logger.LogError(
            "Elasticsearch cluster is still red/unreachable after {Max} attempts. " +
            "Check the cluster health (shard allocation, disk watermark, container restart).",
            maxAttempts);
    }

    private static Properties getProperties()
    {
        return new Properties
        {
            { "id",            new KeywordProperty() },
            // File name is n-gram analyzed for partial-word matching, with a
            // lowercased whole-token search analyzer. A keyword sub-field is kept
            // for exact matching / sorting; "std" holds whole words (CJK characters).
            { "fileName",      new TextProperty
                {
                    Analyzer = NgramIndexAnalyzer,
                    SearchAnalyzer = NgramSearchAnalyzer,
                    Fields = new Properties
                    {
                        { "keyword", new KeywordProperty() },
                        { "std", new TextProperty { Analyzer = WordAnalyzer } }
                    }
                }
            },
            { "shareName",     new KeywordProperty() },
            { "absolutePath",  new KeywordProperty() },
            { "sharePath",     new KeywordProperty() },
            // Content is n-gram analyzed so a substring inside a word (e.g. "Gom"
            // inside "GommeHD") matches. The "std" sub-field keeps a whole-word
            // analysis for relevance ranking, fuzzy matching and CJK characters.
            { "content",       new TextProperty
                {
                    Analyzer = ContentIndexAnalyzer,
                    SearchAnalyzer = ContentSearchAnalyzer,
                    Fields = new Properties
                    {
                        { "std", new TextProperty { Analyzer = WordAnalyzer } }
                    }
                }
            },
            { "fileType",      new KeywordProperty() },
            { "fileSizeBytes", new LongNumberProperty() },
            // Existing indexes map this field dynamically (as long) on first write.
            { "fileModifiedTicks", new LongNumberProperty() },
            { "isDirectory",   new BooleanProperty() },
            { "created",       new DateProperty() },
            { "modified",      new DateProperty() },
            { "author",        new KeywordProperty() },
            { "tags",          new KeywordProperty() },
        };
    }
}
