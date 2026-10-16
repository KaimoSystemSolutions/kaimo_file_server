using System.Collections.Immutable;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Analysis;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Elastic.Transport;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Search;

public class ElasticSearchService : ISearchService
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

    // ══════════════════════════════════════════
    //  Rename / move — keep the index in sync
    // ══════════════════════════════════════════

    public Task onFileRenamed(string oldAbsolutePath, string newAbsolutePath)
    {
        return Task.Run(async () =>
        {
            try
            {
                string oldId = SearchDocuments.GetStableId(oldAbsolutePath);
                var share = await SearchDocuments.ResolveShareAsync(_scopeFactory, newAbsolutePath)
                    ?? throw new InvalidOperationException(
                        $"No share definition contains '{newAbsolutePath}'.");

                var existing = Answered(await _client.GetAsync<FileDocument>(oldId, g => g.Index(_indexName)));
                if (!existing.Found || existing.Source is null)
                {
                    _logger.LogWarning(
                        "Rename: no index document found for '{Old}', skipped",
                        oldAbsolutePath);
                    return;
                }

                var updated = BuildRenamedDocument(
                    existing.Source, newAbsolutePath, share);
                await ReplaceDocumentAsync(oldId, updated);

                _logger.LogDebug("Index updated (file): '{Old}' -> '{New}'",
                    oldAbsolutePath, newAbsolutePath);
            }
            catch (Exception ex) when (ex is not SearchUnavailableException)
            {
                _logger.LogError(ex, "Index rename failed for file '{Old}'", oldAbsolutePath);
            }
        });
    }

    public Task onDirectoryRenamed(string oldAbsolutePath, string newAbsolutePath)
    {
        return Task.Run(async () =>
        {
            try
            {
                string oldPrefix = oldAbsolutePath.TrimEnd(Path.DirectorySeparatorChar)
                                   + Path.DirectorySeparatorChar;
                string newBase = newAbsolutePath.TrimEnd(Path.DirectorySeparatorChar);
                var share = await SearchDocuments.ResolveShareAsync(_scopeFactory, newAbsolutePath)
                    ?? throw new InvalidOperationException(
                        $"No share definition contains '{newAbsolutePath}'.");

                int processed = 0;
                // Safety guard against any unexpected non-shrinking result set.
                int guard = 0;
                const int maxIterations = 100_000;

                while (guard++ < maxIterations)
                {
                    var resp = Answered(await _client.SearchAsync<FileDocument>(s => s
                        .Indices(_indexName)
                        .Size(500)
                        .Query(q => q
                            .Prefix(p => p
                                .Field("absolutePath")
                                .Value(oldPrefix)
                            )
                        )
                    ));

                    if (!resp.IsValidResponse)
                    {
                        _logger.LogError(
                            "Directory rename: search failed for '{Old}': {Error}",
                            oldAbsolutePath, resp.DebugInformation);
                        return;
                    }

                    if (resp.Hits.Count == 0)
                        break;

                    foreach (var hit in resp.Hits)
                    {
                        var doc = hit.Source;
                        if (doc is null) continue;

                        // Path relative to the renamed directory stays identical;
                        // only the directory prefix changes.
                        string remainder = doc.AbsolutePath.Length > oldPrefix.Length
                            ? doc.AbsolutePath.Substring(oldPrefix.Length)
                            : string.Empty;

                        string newAbs = newBase + Path.DirectorySeparatorChar + remainder;

                        var updated = BuildRenamedDocument(doc, newAbs, share);
                        await ReplaceDocumentAsync(doc.Id, updated);
                        processed++;
                    }

                    // Make the deletions/re-indexes visible before the next page,
                    // so the old-prefix result set strictly shrinks and the loop ends.
                    Answered(await _client.Indices.RefreshAsync(_indexName));
                }

                // The renamed directory's OWN document (its absolutePath equals the
                // old path without a trailing separator) is not covered by the
                // child-prefix query above — rewrite it explicitly.
                string oldDirId = SearchDocuments.GetStableId(oldAbsolutePath);
                var ownDoc = Answered(await _client.GetAsync<FileDocument>(oldDirId, g => g.Index(_indexName)));
                if (ownDoc.Found && ownDoc.Source is not null)
                {
                    var updatedOwn = BuildRenamedDocument(
                        ownDoc.Source, newAbsolutePath, share);
                    await ReplaceDocumentAsync(oldDirId, updatedOwn);
                    processed++;
                }

                _logger.LogDebug(
                    "Index updated (directory): '{Old}' -> '{New}' ({Count} document(s))",
                    oldAbsolutePath, newAbsolutePath, processed);
            }
            catch (Exception ex) when (ex is not SearchUnavailableException)
            {
                _logger.LogError(ex, "Index rename failed for directory '{Old}'",
                    oldAbsolutePath);
            }
        });
    }

    /// <summary>
    /// Builds a fresh document for a moved file: recomputes the stable id, share
    /// name, share-relative path, file name and type from the new absolute path,
    /// while preserving the indexed content, size/last-write stamp and original creation time.
    /// </summary>
    internal static FileDocument BuildRenamedDocument(
        FileDocument source,
        string newAbsolutePath,
        ShareDefinition share)
    {
        string sharePath = Path.GetRelativePath(share.Path, newAbsolutePath);

        return new FileDocument
        {
            Id = SearchDocuments.GetStableId(newAbsolutePath),
            FileName = Path.GetFileName(newAbsolutePath).Normalize(),
            ShareName = share.Name,
            AbsolutePath = newAbsolutePath,
            SharePath = sharePath,
            Content = source.Content,
            FileType = source.IsDirectory
                ? string.Empty
                : Path.GetExtension(newAbsolutePath).TrimStart('.'),
            FileSizeBytes = source.FileSizeBytes,
            // A rename keeps content and last-write time, so the next reindex can still skip it.
            FileModifiedTicks = source.FileModifiedTicks,
            IsDirectory = source.IsDirectory,
            Created = source.Created,
            Modified = DateTime.UtcNow,
            Author = source.Author,
            Tags = source.Tags
        };
    }

    /// <summary>
    /// Re-indexes a document under its (new) stable id and removes the old entry
    /// when the id actually changed. Keeps the invariant id == hash(absolutePath).
    /// </summary>
    private async Task ReplaceDocumentAsync(string oldId, FileDocument updated)
    {
        var indexResp = Answered(await _client.IndexAsync(updated, idx => idx
            .Index(_indexName)
            .Id(updated.Id)));

        if (!indexResp.IsValidResponse)
        {
            _logger.LogError("Reindex failed for {Id}: {Error}",
                updated.Id, indexResp.DebugInformation);
            return;
        }

        if (!string.Equals(oldId, updated.Id, StringComparison.Ordinal))
        {
            Answered(await _client.DeleteAsync<FileDocument>(oldId, d => d.Index(_indexName)));
        }
    }

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

    // ══════════════════════════════════════════
    //  Search — ACL is mandatory before any result is returned
    // ══════════════════════════════════════════

    public async Task<List<FileDocument>> SearchAsync(
        string searchText, UserContext user,
        string? shareName = null, string? pathPrefix = null,
        CancellationToken ct = default, bool includeRecycleBin = false)
    {
        // Fail closed: no user context means no results, ever.
        if (user is null)
            return new List<FileDocument>();

        // Page through the ranked hits until enough readable ones are collected, so
        // matches the user may not read cannot crowd out the ones they may.
        var allowed = new List<FileDocument>();
        for (int from = 0; from < SearchLimits.MaxRawScan; from += RawFetchSize)
        {
            var raw = await RawSearchAsync(searchText, shareName, pathPrefix, includeRecycleBin, from, ct);

            // SECURITY: every hit must pass an ACL check before it is exposed.
            if (raw.Count > 0)
                allowed.AddRange(await _aclFilter.FilterAsync(raw, user, ct));

            if (raw.Count < RawFetchSize || allowed.Count >= SearchLimits.MinVisibleHits)
                break;
        }

        return allowed;
    }

    /// <summary>
    /// Executes the raw query. Matches partial words in file names via the n-gram
    /// field and fuzzy whole words in the content. NEVER call this from the UI —
    /// the result is unfiltered and must pass <see cref="FilterByAclAsync"/> first.
    ///
    /// When <paramref name="shareName"/> / <paramref name="pathPrefix"/> are set, the
    /// query is filtered to that share (and folder subtree) via keyword filters.
    /// </summary>
    internal async Task<List<FileDocument>> RawSearchAsync(
        string searchText, string? shareName, string? pathPrefix, bool includeRecycleBin,
        int from, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return new List<FileDocument>();

        // Names and content are indexed in NFC; a query typed on macOS may arrive decomposed.
        searchText = searchText.Normalize();

        var scopeFilters = BuildScopeFilters(shareName, pathPrefix);
        // Recycle-bin documents (any ".RECYCLE_BIN" segment; only the share's/home's own bin
        // is ever indexed) are left out unless requested.
        // ponytail: regexp on the keyword path; add an indexed "inRecycleBin" flag if it gets slow.
        var excludeFilters = includeRecycleBin
            ? new List<Query>()
            : new List<Query>
            {
                new RegexpQuery
                {
                    Field = "sharePath",
                    Value = "(.*/)?[.]" + ShareEntryPolicy.RecycleBinName[1..] + "(/.*)?",
                    CaseInsensitive = true
                }
            };

        var response = await _client.SearchAsync<FileDocument>(s => s
            .Indices(_indexName)
            .From(from)
            .Size(RawFetchSize)
            .Query(q => q
                .Bool(b =>
                {
                    b.Should(
                        // Partial-word matches on the file name (n-gram analyzed).
                        sh => sh.Match(m => m
                            .Field("fileName")
                            .Query(searchText)
                            .Boost(4)
                        ),
                        // Whole words of the file name: single CJK characters are below the
                        // n-gram minimum and only match here.
                        sh => sh.Match(m => m
                            .Field("fileName.std")
                            .Query(searchText)
                            .Boost(4)
                        ),
                        // Substring matches inside the file content (n-gram analyzed).
                        sh => sh.Match(m => m
                            .Field("content")
                            .Query(searchText)
                        ),
                        // Whole-word / fuzzy matches on content for better ranking.
                        sh => sh.Match(m => m
                            .Field("content.std")
                            .Query(searchText)
                            // AUTO:5,8 — no fuzziness below 5 chars. Plain "AUTO"
                            // (=AUTO:3,6) allows 1 edit already at 3 chars, which on
                            // a 3-letter word matches most of the vocabulary and
                            // produces hits with no visible relation to the query.
                            .Fuzziness(new Fuzziness("AUTO:5,8"))
                            .Boost(2)
                        )
                    )
                    .MinimumShouldMatch(1);

                    // Scope to a share / folder subtree without affecting relevance.
                    if (scopeFilters.Count > 0)
                        b.Filter(scopeFilters);
                    if (excludeFilters.Count > 0)
                        b.MustNot(excludeFilters);
                })
            )
            // Matches are marked with private-use characters that indexed text never holds, so a
            // literal "<mark>" in a file cannot pose as a highlight (see SearchDocuments.ToMarkup).
            .Highlight(h => h
                .PreTags(ImmutableList.Create(SearchDocuments.HighlightOpen.ToString()))
                .PostTags(ImmutableList.Create(SearchDocuments.HighlightClose.ToString()))
                .Fields(f => f
                    .Add("content", hf => hf.NumberOfFragments(1).FragmentSize(100))
                    .Add("content.std", hf => hf.NumberOfFragments(1).FragmentSize(100))
                    .Add("fileName", hf => hf.NumberOfFragments(1))
                    .Add("fileName.std", hf => hf.NumberOfFragments(1))
                )
            ), ct);

        if (!response.IsValidResponse)
        {
            // The transport may report a cancelled request as an invalid response;
            // surface it as a cancellation so callers can tell a timeout from "no hits".
            ct.ThrowIfCancellationRequested();
            _logger.LogError("Content search failed: {Error}", response.DebugInformation);
            return new List<FileDocument>();
        }

        var results = new List<FileDocument>();
        foreach (var hit in response.Hits)
        {
            var doc = hit.Source!;
            // The file name when it matched, otherwise the content excerpt.
            string? fragment = null;
            foreach (var field in (ReadOnlySpan<string>)["fileName", "fileName.std", "content", "content.std"])
            {
                if (hit.Highlight?.TryGetValue(field, out var fragments) == true && fragments.FirstOrDefault() is { } first)
                {
                    fragment = first;
                    break;
                }
            }
            doc.HighlightSnippet = SearchDocuments.ToMarkup(fragment);
            results.Add(doc);
        }

        return results;
    }

    /// <summary>
    /// Builds the keyword filters that scope a search to one share and, optionally,
    /// one folder subtree. <c>shareName</c>/<c>sharePath</c> are indexed as keyword
    /// fields, so an exact term and a prefix match cleanly. The folder clause keeps
    /// both the folder itself (exact) and everything below it (prefix + separator),
    /// so a sibling like "reports2" is not swept in by "reports".
    /// </summary>
    private static List<Query> BuildScopeFilters(string? shareName, string? pathPrefix)
    {
        var filters = new List<Query>();
        if (string.IsNullOrWhiteSpace(shareName))
            return filters;

        filters.Add(new TermQuery { Field = "shareName", Value = shareName });

        var prefix = (pathPrefix ?? string.Empty).Replace('\\', '/').Trim('/');
        if (prefix.Length > 0)
        {
            filters.Add(new BoolQuery
            {
                Should = new List<Query>
                {
                    new TermQuery { Field = "sharePath", Value = prefix },
                    new PrefixQuery { Field = "sharePath", Value = prefix + "/" }
                },
                MinimumShouldMatch = 1
            });
        }

        return filters;
    }

    // ══════════════════════════════════════════
    //  Indexing
    // ══════════════════════════════════════════

    /// <summary>
    /// True when the index already holds this file at the same path with the same size and
    /// last-write time, so its content need not be read or extracted again (mirrors the local
    /// index). Documents indexed before timestamps were stored carry none and are rewritten once.
    /// </summary>
    private async Task<bool> IsIndexedUnchangedAsync(FileDocument document, CancellationToken ct)
    {
        // The stored content is not needed for the comparison; it can be up to MaxContentChars.
        var existing = Answered(await _client.GetAsync<FileDocument>(document.Id, g => g
            .Index(_indexName)
            .SourceExcludes("content"), ct));

        return existing.Found
               && existing.Source is { } indexed
               && indexed.AbsolutePath == document.AbsolutePath
               && indexed.FileName == document.FileName
               && indexed.FileSizeBytes == document.FileSizeBytes
               && indexed.FileModifiedTicks == document.FileModifiedTicks;
    }

    private async Task IndexDocumentIfNotExistsAsync(
        string absolutePath,
        Task<Stream> fileData,
        CancellationToken ct = default,
        ShareDefinition? knownShare = null)
    {
        string fileName = Path.GetFileName(absolutePath);
        var share = knownShare ?? await SearchDocuments.ResolveShareAsync(_scopeFactory, absolutePath);
        if (share is null)
        {
            await using var unused = await fileData;
            _logger.LogWarning(
                "Indexing skipped because no share definition contains '{Path}'",
                absolutePath);
            return;
        }

        FileDocument document = new FileDocument
        {
            Id = SearchDocuments.GetStableId(absolutePath),
            // NFC like content and queries: macOS clients create decomposed names.
            FileName = fileName.Normalize(),
            ShareName = share.Name,
            AbsolutePath = absolutePath,
            SharePath = Path.GetRelativePath(share.Path, absolutePath),
            FileType = Path.GetExtension(fileName).TrimStart('.'),
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        };

        // IMPORTANT: dispose the read stream immediately. Leaving the FileStream
        // open keeps an OS handle on the file, which on Windows-backed bind mounts
        // blocks renaming/moving the containing directory ("access denied").
        await using (var fileStream = await fileData)
        {
            var info = new FileInfo(absolutePath);
            document.FileSizeBytes = info.Length; // throws FileNotFoundException when the file is gone
            // An SMB file still locked by its writer arrives as Stream.Null (name-only). Store no
            // stamp then, so the Modified entry on close re-extracts the real content.
            document.FileModifiedTicks = ReferenceEquals(fileStream, Stream.Null) ? null : info.LastWriteTimeUtc.Ticks;

            if (document.FileModifiedTicks is not null && await IsIndexedUnchangedAsync(document, ct))
            {
                _logger.LogDebug("Document {Id} already indexed and unchanged, skipping", document.Id);
                return;
            }

            document.Content = await ContentProvider.GetContent(fileStream, fileName, _logger);
        }
        if (document.Content.Length > MaxContentChars)
            // Never cut a surrogate pair in half: a lone high surrogate is invalid UTF-16.
            document.Content = document.Content[..(char.IsHighSurrogate(document.Content[MaxContentChars - 1])
                ? MaxContentChars - 1
                : MaxContentChars)];

        // No delete first: indexing under the same id replaces the document, and a
        // delete would leave the file unfindable if the engine dies during the write.
        _logger.LogDebug("Indexing document {Id}", document.Id);

        var response = Answered(await _client.IndexAsync(document, idx => idx
            .Index(_indexName)
            .Id(document.Id),
            ct));

        // A document Elasticsearch rejects because of its content (e.g. a huge n-gram token
        // stream) must not take the file name with it: retry name-only. No answer at all already
        // threw above — that is an outage, not this document's fault, and is retried by the caller.
        if (!response.IsValidResponse && document.Content.Length > 0)
        {
            _logger.LogWarning("Indexing with content failed for {Id}, retrying name-only: {Error}",
                document.Id, response.DebugInformation);
            document.Content = string.Empty;
            response = Answered(await _client.IndexAsync(document, idx => idx
                .Index(_indexName)
                .Id(document.Id),
                ct));
        }

        if (!response.IsValidResponse)
            _logger.LogError("Indexing failed for {Id}: {Error}",
                document.Id, response.DebugInformation);
    }

    /// <summary>
    /// Re-indexes every file currently on disk under the persisted share paths. Used to
    /// catch up files that arrived while indexing was off, or were added out of
    /// band (e.g. directly on the volume). Already-indexed files with the same size and
    /// last-write time are skipped without being read, so a reindex is cheap to repeat.
    /// Hidden/system folders (".versions", ".dp-keys", …) are skipped; the recycle bin is indexed.
    /// After a complete pass, documents of the walked shares whose files are gone (deleted or
    /// moved out of band) are swept, like the local index does. A canceled pass sweeps nothing.
    /// </summary>
    public async Task ReindexAllAsync(
        IProgress<(int done, int total)>? progress, CancellationToken ct = default,
        string? shareName = null)
    {
        var started = DateTime.UtcNow;
        // Ensure the index exists with the correct (n-gram) mapping before writing.
        await InitializeAsync(ct);

        var targets = await SearchDocuments.CollectReindexTargetsAsync(_scopeFactory, shareName);
        var files = targets.Files;
        var dirs = targets.Directories;

        int total = files.Count + dirs.Count;
        int done = 0;
        var failures = new ReindexFailureLog(_logger);
        progress?.Report((0, total));

        // An outage (SearchUnavailableException) aborts the pass: every further entry would fail
        // the same way. The router reports it once as a failed reindex; unchanged entries are
        // skipped cheaply when it is restarted.
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Task<Stream> data = Task.FromResult<Stream>(new FileStream(
                    file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true));
                await IndexDocumentIfNotExistsAsync(file.Path, data, ct, file.Share);
            }
            catch (Exception ex) when (ex is not SearchUnavailableException)
            {
                failures.Add(file.Path, ex);
            }

            done++;
            // Report every item: indexing a single large file (content extraction
            // + ES round-trip) can take seconds, so a coarser interval would leave
            // the progress bar frozen between updates. The report only sets an
            // in-memory field, so per-item cost is negligible.
            progress?.Report((done, total));
        }

        foreach (var directory in dirs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var doc = SearchDocuments.BuildDirectoryDocument(directory.Path, directory.Share);
                if (doc is not null)
                    await IndexDirectoryDocumentAsync(doc, ct);
            }
            catch (Exception ex) when (ex is not SearchUnavailableException)
            {
                failures.Add(directory.Path, ex);
            }

            done++;
            progress?.Report((done, total));
        }

        failures.LogSummary(total);

        ct.ThrowIfCancellationRequested();
        int swept = await SweepAsync(targets, started, ct);

        _logger.LogDebug(
            "Reindex completed: {Done}/{Total} entries ({Files} files, {Dirs} directories), {Swept} stale document(s) removed",
            done, total, files.Count, dirs.Count, swept);
    }

    /// <summary>
    /// Deletes the documents of the walked shares that the pass did not visit: their files were
    /// deleted or moved directly on the volume, so no change-log entry ever removed them.
    /// Documents written since <paramref name="startedUtc"/> (a change indexed during the pass)
    /// are kept. Entries that failed to index were visited and are kept as well.
    /// </summary>
    private async Task<int> SweepAsync(ReindexTargets targets, DateTime startedUtc, CancellationToken ct)
    {
        if (targets.Shares.Count == 0)
            return 0;

        var visited = targets.Files.Concat(targets.Directories)
            .Select(entry => SearchDocuments.GetStableId(entry.Path))
            .ToHashSet(StringComparer.Ordinal);

        Answered(await _client.Indices.RefreshAsync(_indexName, ct));

        var inShares = new BoolQuery
        {
            Should = targets.Shares.Select(s => (Query)new TermQuery { Field = "shareName", Value = s.Name }).ToList(),
            MinimumShouldMatch = 1
        };

        var stale = new List<string>();
        ICollection<FieldValue>? after = null;
        while (true)
        {
            var page = Answered(await _client.SearchAsync<SweepHit>(new SearchRequest(_indexName)
            {
                Size = SweepPageSize,
                Query = inShares,
                Sort = [new SortOptions { Field = new FieldSort { Field = "id", Order = SortOrder.Asc } }],
                SearchAfter = after,
                Source = new SourceConfig(new SourceFilter { Includes = new[] { "modified" } })
            }, ct));

            if (!page.IsValidResponse)
            {
                _logger.LogError("Sweeping stale search documents failed: {Error}", page.DebugInformation);
                return 0;
            }

            foreach (var hit in page.Hits)
            {
                if (hit.Id is { } id && !visited.Contains(id) && hit.Source?.Modified < startedUtc)
                    stale.Add(id);
            }

            if (page.Hits.Count < SweepPageSize)
                break;
            after = page.Hits.Last().Sort?.ToList();
        }

        // ponytail: one request per stale document; out-of-band deletions are usually few.
        // Switch to a bulk delete if sweeps of whole emptied shares become common.
        foreach (var id in stale)
        {
            ct.ThrowIfCancellationRequested();
            Answered(await _client.DeleteAsync<FileDocument>(id, d => d.Index(_indexName), ct));
        }

        if (stale.Count > 0)
            Answered(await _client.Indices.RefreshAsync(_indexName, ct));
        return stale.Count;
    }

    /// <summary>The one field the sweep reads (<see cref="FileDocument"/> requires them all).</summary>
    private sealed class SweepHit
    {
        public DateTime Modified { get; set; }
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
