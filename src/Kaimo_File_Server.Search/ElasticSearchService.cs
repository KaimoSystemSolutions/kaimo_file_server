using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Analysis;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Search;

public class ElasticSearchService : ISearchService
{
    // NOTE: the index name is versioned. Changing a field's analyzer requires a
    // NEW index (analyzers are fixed at field-creation time). Bumping the suffix
    // forces InitializeAsync to build a fresh index with the current mapping.
    // The old "kaimo-files" index can be deleted manually; existing files are
    // re-indexed as they are written/renamed.
    private const string IndexName = "kaimo-files-v2";

    // Index-time analyzer: breaks tokens into n-grams so a substring of a word
    // (e.g. "ocum" inside "document") still produces a matching token.
    private const string NgramIndexAnalyzer = "kaimo_ngram_index";
    // Search-time analyzer: keeps the user's query as whole tokens (lowercased)
    // so it matches against the indexed n-grams without exploding into noise.
    private const string NgramSearchAnalyzer = "kaimo_ngram_search";

    private const int NgramMinGram = 2;
    private const int NgramMaxGram = 20;

    // How many results to pull from ES before ACL filtering. We over-fetch so that
    // dropping unauthorized hits still leaves enough to display.
    private const int RawFetchSize = 50;

    private readonly ElasticsearchClient _client;
    private readonly ILogger<ElasticSearchService> _logger;
    private readonly IStorageEngine _storage;
    private readonly SearchAclFilter _aclFilter;

    private enum ExistsResult
    {
        ExactFileExists,
        OldVersionExists,
        DoesntExist
    }

    public ElasticSearchService(
        ElasticsearchClient client,
        ILogger<ElasticSearchService> logger,
        IStorageEngine storage,
        SearchAclFilter aclFilter)
    {
        _client = client;
        _logger = logger;
        _storage = storage;
        _aclFilter = aclFilter;
    }

    public async Task onFileCreated(string absolutePath, Task<Stream> fileData, CancellationToken ct = default)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await IndexDocumentIfNotExistsAsync(absolutePath, fileData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Indexing failed for {Path}", absolutePath);
            }
        });
    }

    public async Task onFileDeleted(string absolutePath)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var response = await _client.DeleteByQueryAsync<FileDocument>(IndexName, d => d
                    .Query(q => q
                        .Term(t => t
                            .Field("absolutePath")
                            .Value(absolutePath)
                        )
                    )
                );

                if (response.Deleted == 0)
                    _logger.LogWarning("No document found for path '{Path}'", absolutePath);
                else
                    _logger.LogInformation("Removed document for '{Path}' from the index", absolutePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deletion failed for {Path}", absolutePath);
            }
        });
    }

    public Task onDirectoryCreated(string absolutePath)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var doc = BuildDirectoryDocument(absolutePath);
                if (doc is null)
                {
                    // Share-root directory (or the storage root itself) — not a
                    // searchable folder within a share, so nothing to index.
                    _logger.LogDebug("Directory create (not indexed): '{Path}'", absolutePath);
                    return;
                }

                await IndexDirectoryDocumentAsync(doc);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Directory indexing failed for {Path}", absolutePath);
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds an index document describing a directory: name only, no content.
    /// Returns <c>null</c> for the storage root and for share-root directories —
    /// only folders *inside* a share are searchable entities.
    /// </summary>
    private FileDocument? BuildDirectoryDocument(string absolutePath)
    {
        string rootPath = _storage.getRootPath();
        string relativePath = Path.GetRelativePath(rootPath, absolutePath);

        var segments = relativePath.Split(
            Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        // segments.Length < 2  →  storage root ("." / "") or a share root.
        if (segments.Length < 2)
            return null;

        string shareName = segments[0];
        string sharePath = Path.GetRelativePath(Path.Combine(rootPath, shareName), absolutePath);

        return new FileDocument
        {
            Id = GetStableId(absolutePath),
            FileName = Path.GetFileName(absolutePath.TrimEnd(Path.DirectorySeparatorChar)),
            ShareName = shareName,
            AbsolutePath = absolutePath,
            SharePath = sharePath,
            Content = string.Empty,
            FileType = string.Empty,
            FileSizeBytes = 0,
            IsDirectory = true,
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Indexes a directory document if it is not already present. A folder has no
    /// content that could change, so an existing entry is left untouched (renames
    /// are handled separately via <see cref="onDirectoryRenamed"/>).
    /// </summary>
    private async Task IndexDirectoryDocumentAsync(FileDocument doc, CancellationToken ct = default)
    {
        var existing = await _client.GetAsync<FileDocument>(doc.Id, g => g.Index(IndexName), ct);
        if (existing.Found)
        {
            _logger.LogDebug("Directory {Id} already indexed, skipping", doc.Id);
            return;
        }

        var response = await _client.IndexAsync(doc, idx => idx
            .Index(IndexName)
            .Id(doc.Id), ct);

        if (!response.IsValidResponse)
            _logger.LogError("Directory indexing failed for {Id}: {Error}",
                doc.Id, response.DebugInformation);
    }

    public async Task onDirectoryDeleted(string absolutePath)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                string prefix = absolutePath.TrimEnd(Path.DirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;

                var response = await _client.DeleteByQueryAsync<FileDocument>(IndexName, d => d
                    .Query(q => q
                        .Bool(b => b
                            .Should(
                                s => s.Term(t => t.Field("absolutePath").Value(absolutePath)),
                                s => s.Prefix(p => p.Field("absolutePath").Value(prefix))
                            )
                            .MinimumShouldMatch(1)
                        )
                    )
                );

                if (!response.IsValidResponse)
                    _logger.LogError("DeleteByQuery failed for directory '{Path}': {Error}",
                        absolutePath, response.DebugInformation);
                else
                    _logger.LogInformation(
                        "{Count} Dokument(e) für Verzeichnis '{Path}' aus Index entfernt",
                        response.Deleted, absolutePath);
            }
            catch (Exception ex)
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
        _ = Task.Run(async () =>
        {
            try
            {
                string oldId = GetStableId(oldAbsolutePath);

                var existing = await _client.GetAsync<FileDocument>(oldId, g => g.Index(IndexName));
                if (!existing.Found || existing.Source is null)
                {
                    _logger.LogWarning(
                        "Umbenennung: kein Index-Dokument für '{Old}' gefunden — übersprungen",
                        oldAbsolutePath);
                    return;
                }

                var updated = BuildRenamedDocument(existing.Source, newAbsolutePath);
                await ReplaceDocumentAsync(oldId, updated);

                _logger.LogInformation("Index updated (file): '{Old}' -> '{New}'",
                    oldAbsolutePath, newAbsolutePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Index rename failed for file '{Old}'", oldAbsolutePath);
            }
        });
        return Task.CompletedTask;
    }

    public Task onDirectoryRenamed(string oldAbsolutePath, string newAbsolutePath)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                string oldPrefix = oldAbsolutePath.TrimEnd(Path.DirectorySeparatorChar)
                                   + Path.DirectorySeparatorChar;
                string newBase = newAbsolutePath.TrimEnd(Path.DirectorySeparatorChar);

                int processed = 0;
                // Safety guard against any unexpected non-shrinking result set.
                int guard = 0;
                const int maxIterations = 100_000;

                while (guard++ < maxIterations)
                {
                    var resp = await _client.SearchAsync<FileDocument>(s => s
                        .Indices(IndexName)
                        .Size(500)
                        .Query(q => q
                            .Prefix(p => p
                                .Field("absolutePath")
                                .Value(oldPrefix)
                            )
                        )
                    );

                    if (!resp.IsValidResponse)
                    {
                        _logger.LogError(
                            "Verzeichnis-Umbenennung: Suche fehlgeschlagen für '{Old}': {Error}",
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

                        var updated = BuildRenamedDocument(doc, newAbs);
                        await ReplaceDocumentAsync(doc.Id, updated);
                        processed++;
                    }

                    // Make the deletions/re-indexes visible before the next page,
                    // so the old-prefix result set strictly shrinks and the loop ends.
                    await _client.Indices.RefreshAsync(IndexName);
                }

                // The renamed directory's OWN document (its absolutePath equals the
                // old path without a trailing separator) is not covered by the
                // child-prefix query above — rewrite it explicitly.
                string oldDirId = GetStableId(oldAbsolutePath);
                var ownDoc = await _client.GetAsync<FileDocument>(oldDirId, g => g.Index(IndexName));
                if (ownDoc.Found && ownDoc.Source is not null)
                {
                    var updatedOwn = BuildRenamedDocument(ownDoc.Source, newAbsolutePath);
                    await ReplaceDocumentAsync(oldDirId, updatedOwn);
                    processed++;
                }

                _logger.LogInformation(
                    "Index aktualisiert (Verzeichnis): '{Old}' -> '{New}' ({Count} Dokument(e))",
                    oldAbsolutePath, newAbsolutePath, processed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Index rename failed for directory '{Old}'",
                    oldAbsolutePath);
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds a fresh document for a moved file: recomputes the stable id, share
    /// name, share-relative path, file name and type from the new absolute path,
    /// while preserving the indexed content and original creation time.
    /// </summary>
    private FileDocument BuildRenamedDocument(FileDocument source, string newAbsolutePath)
    {
        string rootPath = _storage.getRootPath();
        string relativePath = Path.GetRelativePath(rootPath, newAbsolutePath);
        string shareName = relativePath.Split(Path.DirectorySeparatorChar)[0];
        string sharePath = Path.GetRelativePath(Path.Combine(rootPath, shareName), newAbsolutePath);

        return new FileDocument
        {
            Id = GetStableId(newAbsolutePath),
            FileName = Path.GetFileName(newAbsolutePath),
            ShareName = shareName,
            AbsolutePath = newAbsolutePath,
            SharePath = sharePath,
            Content = source.Content,
            FileType = source.IsDirectory
                ? string.Empty
                : Path.GetExtension(newAbsolutePath).TrimStart('.'),
            FileSizeBytes = source.FileSizeBytes,
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
        var indexResp = await _client.IndexAsync(updated, idx => idx
            .Index(IndexName)
            .Id(updated.Id));

        if (!indexResp.IsValidResponse)
        {
            _logger.LogError("Reindex failed for {Id}: {Error}",
                updated.Id, indexResp.DebugInformation);
            return;
        }

        if (!string.Equals(oldId, updated.Id, StringComparison.Ordinal))
        {
            await _client.DeleteAsync<FileDocument>(oldId, d => d.Index(IndexName));
        }
    }

    // ══════════════════════════════════════════
    //  Index lifecycle
    // ══════════════════════════════════════════

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Don't touch the index until the cluster can actually allocate shards.
        // Creating an index against an unhealthy (red) node leaves its primary
        // shard unassigned -> every later _search returns 503 "all shards failed".
        await WaitForClusterReadyAsync(ct);

        var exists = await _client.Indices.ExistsAsync(IndexName, ct);
        if (exists.Exists)
            return;

        var createRequest = new CreateIndexRequest(IndexName)
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
                    Tokenizers = new Tokenizers
                    {
                        {
                            "kaimo_ngram_tokenizer", new NGramTokenizer
                            {
                                MinGram = NgramMinGram,
                                MaxGram = NgramMaxGram,
                                TokenChars = new[] { TokenChar.Letter, TokenChar.Digit }
                            }
                        }
                    },
                    Analyzers = new Analyzers
                    {
                        {
                            NgramIndexAnalyzer, new CustomAnalyzer
                            {
                                Tokenizer = "kaimo_ngram_tokenizer",
                                Filter = new[] { "lowercase" }
                            }
                        },
                        {
                            NgramSearchAnalyzer, new CustomAnalyzer
                            {
                                Tokenizer = "standard",
                                Filter = new[] { "lowercase" }
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
            _logger.LogError("Index could not be created: {Error}", response.DebugInformation);
        else
            _logger.LogInformation("Index '{Index}' created", IndexName);
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
                        _logger.LogInformation("Elasticsearch-Cluster bereit (Status {Status})", health.Status);
                    return;
                }

                _logger.LogWarning(
                    "Elasticsearch-Cluster noch nicht bereit (Status rot), Versuch {Attempt}/{Max}",
                    attempt, maxAttempts);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Elasticsearch noch nicht erreichbar, Versuch {Attempt}/{Max}", attempt, maxAttempts);
            }

            await Task.Delay(2000, ct);
        }

        _logger.LogError(
            "Elasticsearch-Cluster ist nach {Max} Versuchen weiterhin rot/unerreichbar. " +
            "Bitte ES-Health prüfen (Shard-Allocation / Disk-Watermark / Container-Neustart).",
            maxAttempts);
    }

    // ══════════════════════════════════════════
    //  Search — ACL is mandatory before any result is returned
    // ══════════════════════════════════════════

    public async Task<List<FileDocument>> SearchAsync(
        string searchText, UserContext user, CancellationToken ct = default)
    {
        // Fail closed: no user context means no results, ever.
        if (user is null)
            return new List<FileDocument>();

        var raw = await RawSearchAsync(searchText, ct);
        if (raw.Count == 0)
            return raw;

        // SECURITY: every hit must pass an ACL check before it is exposed.
        return await _aclFilter.FilterAsync(raw, user, ct);
    }

    /// <summary>
    /// Executes the raw query. Matches partial words in file names via the n-gram
    /// field and fuzzy whole words in the content. NEVER call this from the UI —
    /// the result is unfiltered and must pass <see cref="FilterByAclAsync"/> first.
    /// </summary>
    private async Task<List<FileDocument>> RawSearchAsync(string searchText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return new List<FileDocument>();

        var response = await _client.SearchAsync<FileDocument>(s => s
            .Indices(IndexName)
            .Size(RawFetchSize)
            .Query(q => q
                .Bool(b => b
                    .Should(
                        // Partial-word matches on the file name (n-gram analyzed).
                        sh => sh.Match(m => m
                            .Field("fileName")
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
                            .Fuzziness(new Fuzziness("AUTO"))
                            .Boost(2)
                        )
                    )
                    .MinimumShouldMatch(1)
                )
            )
            .Highlight(h => h
                .Fields(f => f
                    .Add("content", hf => hf
                        .NumberOfFragments(1)
                        .FragmentSize(100)
                        .PreTags(ImmutableList.Create("<mark>"))
                        .PostTags(ImmutableList.Create("</mark>"))
                    )
                    .Add("fileName", hf => hf
                        .NumberOfFragments(1)
                        .PreTags(ImmutableList.Create("<mark>"))
                        .PostTags(ImmutableList.Create("</mark>"))
                    )
                )
            ), ct);

        if (!response.IsValidResponse)
        {
            _logger.LogError("Content search failed: {Error}", response.DebugInformation);
            return new List<FileDocument>();
        }

        var results = new List<FileDocument>();
        foreach (var hit in response.Hits)
        {
            var doc = hit.Source!;
            if (hit.Highlight?.TryGetValue("fileName", out var nameFragments) == true && nameFragments.Any())
            {
                doc.HighlightSnippet = nameFragments.First();
            }
            else if (hit.Highlight?.TryGetValue("content", out var fragments) == true && fragments.Any())
            {
                doc.HighlightSnippet = fragments.First();
            }
            results.Add(doc);
        }

        return results;
    }

    // ══════════════════════════════════════════
    //  Indexing
    // ══════════════════════════════════════════

    private async Task<ExistsResult> CheckForDocumentsExistance(FileDocument document, CancellationToken ct = default)
    {

        var existing = await _client.GetAsync<FileDocument>(document.Id, g => g
            .Index(IndexName));

        if (!existing.Found)
            return ExistsResult.DoesntExist;

        var existingFile = existing.Source;

        bool sameContent = document.Content.Equals(existingFile?.Content);
        bool samePath = document.AbsolutePath.Equals(existingFile?.AbsolutePath) && document.FileName.Equals(existingFile.FileName);

        if (samePath && sameContent)
            return ExistsResult.ExactFileExists;

        if (samePath)
            return ExistsResult.OldVersionExists;

        return ExistsResult.DoesntExist;
    }

    private async Task IndexDocumentIfNotExistsAsync(string absolutePath, Task<Stream> fileData, CancellationToken ct = default)
    {
        string fileName = Path.GetFileName(absolutePath);

        // IMPORTANT: dispose the read stream immediately. Leaving the FileStream
        // open keeps an OS handle on the file, which on Windows-backed bind mounts
        // blocks renaming/moving the containing directory ("access denied").
        string content;
        await using (var fileStream = await fileData)
        {
            content = await ContentProvider.GetContent(fileStream);
        }

        string rootPath = _storage.getRootPath();
        string relativePath = Path.GetRelativePath(rootPath, absolutePath);
        string shareName = relativePath.Split(Path.DirectorySeparatorChar)[0];
        string sharePath = Path.GetRelativePath(Path.Combine(rootPath, shareName), absolutePath);

        FileDocument document = new FileDocument
        {
            Id = GetStableId(absolutePath),
            FileName = fileName,
            ShareName = shareName,
            AbsolutePath = absolutePath,
            SharePath = sharePath,
            Content = content,
            FileType = Path.GetExtension(fileName).TrimStart('.'),
            FileSizeBytes = new FileInfo(absolutePath).Length,
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        };

        ExistsResult existenceCheck = await CheckForDocumentsExistance(document, ct);

        switch (existenceCheck)
        {
            case ExistsResult.ExactFileExists:
                _logger.LogInformation("Document {Id} already indexed, skipping", document.Id);
                return;

            case ExistsResult.OldVersionExists:
                _logger.LogInformation("Found older version of {Id}, overwriting it", document.Id);
                await _client.DeleteAsync<FileDocument>(document.Id, d => d.Index(IndexName), ct);
                break;

            case ExistsResult.DoesntExist:
                _logger.LogInformation("Indexing new document {Id}", document.Id);
                break;
        }

        var response = await _client.IndexAsync(document, idx => idx
            .Index(IndexName)
            .Id(document.Id),
            ct);

        if (!response.IsValidResponse)
            _logger.LogError("Indexing failed for {Id}: {Error}",
                document.Id, response.DebugInformation);
    }

    /// <summary>
    /// Re-indexes every file currently on disk under the storage root. Used to
    /// catch up files that arrived while indexing was off, or were added out of
    /// band (e.g. directly on the volume). Already-indexed, unchanged files are
    /// skipped by the existing dedupe check, so a reindex is cheap to repeat.
    /// Hidden/system folders (".versions", ".dp-keys", recycle bin, …) are skipped.
    /// </summary>
    public async Task ReindexAllAsync(
        IProgress<(int done, int total)>? progress, CancellationToken ct = default)
    {
        // Ensure the index exists with the correct (n-gram) mapping before writing.
        await InitializeAsync(ct);

        string rootPath = _storage.getRootPath();
        if (!Directory.Exists(rootPath))
            return;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System
        };

        var files = Directory.EnumerateFiles(rootPath, "*", options)
            .Where(p => !IsHiddenRelative(rootPath, p))
            .ToList();

        // Directories are indexed too (by name), so folders show up in search.
        // Share-root directories are excluded via BuildDirectoryDocument (they are
        // not searchable entities inside a share).
        var dirs = Directory.EnumerateDirectories(rootPath, "*", options)
            .Where(p => !IsHiddenRelative(rootPath, p))
            .ToList();

        int total = files.Count + dirs.Count;
        int done = 0;
        progress?.Report((0, total));

        foreach (var absolutePath in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Task<Stream> data = Task.FromResult<Stream>(new FileStream(
                    absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true));
                await IndexDocumentIfNotExistsAsync(absolutePath, data, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reindex: indexing failed for '{Path}'", absolutePath);
            }

            done++;
            if (done % 25 == 0 || done == total)
                progress?.Report((done, total));
        }

        foreach (var absolutePath in dirs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var doc = BuildDirectoryDocument(absolutePath);
                if (doc is not null)
                    await IndexDirectoryDocumentAsync(doc, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reindex: indexing failed for directory '{Path}'", absolutePath);
            }

            done++;
            if (done % 25 == 0 || done == total)
                progress?.Report((done, total));
        }

        _logger.LogInformation(
            "Reindex abgeschlossen: {Done}/{Total} Einträge ({Files} Dateien, {Dirs} Verzeichnisse)",
            done, total, files.Count, dirs.Count);
    }

    private static bool IsHiddenRelative(string rootPath, string absolutePath)
    {
        var relativePath = Path.GetRelativePath(rootPath, absolutePath);
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar))
            if (segment.StartsWith('.')) return true;
        return false;
    }

    private static string GetStableId(string absolutePath)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(absolutePath));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        var response = await _client.SearchAsync<FileDocument>(s => s
            .Indices(IndexName)
            .From(request.From)
            .Size(request.Size)
            .Query(q => BuildQuery(q, request)),
            ct);

        if (!response.IsValidResponse)
        {
            _logger.LogError("Search failed: {Error}", response.DebugInformation);
            return new SearchResult();
        }

        return new SearchResult
        {
            TotalHits = response.Total,
            Hits = response.Hits.Select(h => new SearchHit
            {
                Document = h.Source!,
                Score = h.Score ?? 0
            }).ToList()
        };
    }

    private static Action<QueryDescriptor<FileDocument>> BuildQuery(
        QueryDescriptor<FileDocument> q, SearchRequest request)
    {
        return _ => q.Bool(b =>
        {
            if (!string.IsNullOrWhiteSpace(request.Query))
            {
                b.Must(m => m
                    .Bool(inner => inner
                        .Should(
                            sh => sh.Match(mm => mm
                                .Field("fileName")
                                .Query(request.Query)
                                .Boost(3)),
                            sh => sh.Match(mm => mm
                                .Field("content")
                                .Query(request.Query)
                                .Fuzziness(new Fuzziness("AUTO")))
                        )
                        .MinimumShouldMatch(1)
                    )
                );
            }

            var filters = new List<Action<QueryDescriptor<FileDocument>>>();

            if (!string.IsNullOrWhiteSpace(request.FileType))
            {
                filters.Add(f => f.Term(t => t
                    .Field("fileType")
                    .Value(request.FileType)));
            }

            if (request.CreatedAfter.HasValue || request.CreatedBefore.HasValue)
            {
                filters.Add(f => f.Range(r => r
                    .Date(d =>
                    {
                        d.Field("created");
                        if (request.CreatedAfter.HasValue)
                            d.Gte(request.CreatedAfter.Value);
                        if (request.CreatedBefore.HasValue)
                            d.Lte(request.CreatedBefore.Value);
                    })));
            }

            if (filters.Count > 0)
                b.Filter(filters.ToArray());
        });
    }


    private static Properties getProperties()
    {
        return new Properties
        {
            { "id",            new KeywordProperty() },
            // File name is n-gram analyzed for partial-word matching, with a
            // lowercased whole-token search analyzer. A keyword sub-field is kept
            // for exact matching / sorting.
            { "fileName",      new TextProperty
                {
                    Analyzer = NgramIndexAnalyzer,
                    SearchAnalyzer = NgramSearchAnalyzer,
                    Fields = new Properties
                    {
                        { "keyword", new KeywordProperty() }
                    }
                }
            },
            { "shareName",     new KeywordProperty() },
            { "absolutePath",  new KeywordProperty() },
            { "sharePath",     new KeywordProperty() },
            // Content is n-gram analyzed so a substring inside a word (e.g. "Gom"
            // inside "GommeHD") matches. The "std" sub-field keeps a whole-word
            // standard analysis for relevance ranking and fuzzy matching.
            { "content",       new TextProperty
                {
                    Analyzer = NgramIndexAnalyzer,
                    SearchAnalyzer = NgramSearchAnalyzer,
                    Fields = new Properties
                    {
                        { "std", new TextProperty { Analyzer = "standard" } }
                    }
                }
            },
            { "fileType",      new KeywordProperty() },
            { "fileSizeBytes", new LongNumberProperty() },
            { "isDirectory",   new BooleanProperty() },
            { "created",       new DateProperty() },
            { "modified",      new DateProperty() },
            { "author",        new KeywordProperty() },
            { "tags",          new KeywordProperty() },
        };
    }
}
