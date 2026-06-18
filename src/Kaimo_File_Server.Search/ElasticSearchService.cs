using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Kaimo_File_Server.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Search;

public class ElasticSearchService : ISearchService
{
    private const string IndexName = "kaimo-files";

    private readonly ElasticsearchClient _client;
    private readonly ILogger<ElasticSearchService> _logger;
    private readonly IStorageEngine _storage;

    private enum ExistsResult
    {
        ExactFileExists,
        OldVersionExists,
        DoesntExist
    }
    
    public ElasticSearchService(ElasticsearchClient client, ILogger<ElasticSearchService> logger, IStorageEngine storage)
    {
        _client = client;
        _logger = logger;
        _storage = storage;
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
                _logger.LogError(ex, "Indexierung fehlgeschlagen für {Path}", absolutePath);
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
                            .Field("filePath")
                            .Value(absolutePath)
                        )
                    )
                );
    
                if (response.Deleted == 0)
                    _logger.LogWarning("Kein Dokument für Pfad '{Path}' gefunden", absolutePath);
                else
                    _logger.LogInformation("Dokument für '{Path}' aus Index entfernt", absolutePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Löschen fehlgeschlagen für {Path}", absolutePath);
            }
        });
    }
    
    public Task onDirectoryCreated(string absolutePath)
    {
        _logger.LogDebug("Verzeichnis erstellt (keine Aktion): '{Path}'", absolutePath);
        return Task.CompletedTask;
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
                                s => s.Term(t => t.Field("filePath").Value(absolutePath)),
                                s => s.Prefix(p => p.Field("filePath").Value(prefix))
                            )
                            .MinimumShouldMatch(1)
                        )
                    )
                );
    
                if (!response.IsValidResponse)
                    _logger.LogError("DeleteByQuery fehlgeschlagen für Verzeichnis '{Path}': {Error}",
                        absolutePath, response.DebugInformation);
                else
                    _logger.LogInformation(
                        "{Count} Dokument(e) für Verzeichnis '{Path}' aus Index entfernt",
                        response.Deleted, absolutePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Löschen des Verzeichnisses fehlgeschlagen für {Path}", absolutePath);
            }
        });
    }
        
        
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var exists = await _client.Indices.ExistsAsync(IndexName, ct);
        if (exists.Exists)
            return;

        var response = await _client.Indices.CreateAsync(IndexName, c => c
            .Mappings(m => m
                .Properties(getProperties())
            ), ct);

        if (!response.IsValidResponse)
            _logger.LogError("Index konnte nicht erstellt werden: {Error}", response.DebugInformation);
        else
            _logger.LogInformation("Index '{Index}' erstellt", IndexName);
    }
    
    public async Task<List<FileDocument>> SearchAsync(string searchText, CancellationToken ct = default)
    {
        var response = await _client.SearchAsync<FileDocument>(s => s
            .Index(IndexName)
            .Query(q => q
                .MultiMatch(mm => mm
                    .Query(searchText)
                    .Fields(new[] { "content", "fileName" })
                    .Fuzziness(new Fuzziness("AUTO"))
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
            _logger.LogError("Inhaltssuche fehlgeschlagen: {Error}", response.DebugInformation);
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
        
        string content = await ContentProvider.GetContent(await fileData);

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
                _logger.LogInformation("Dokument {Id} bereits indexiert, überspringe", document.Id);
                return;

            case ExistsResult.OldVersionExists:
                _logger.LogInformation("Alte Version von {Id} gefunden, wird überschrieben", document.Id);
                await _client.DeleteAsync<FileDocument>(document.Id, d => d.Index(IndexName), ct);
                break;

            case ExistsResult.DoesntExist:
                _logger.LogInformation("Neues Dokument {Id} wird indexiert", document.Id);
                break;
        }
        
        var response = await _client.IndexAsync(document, idx => idx
            .Index(IndexName)
            .Id(document.Id),
            ct);

        if (!response.IsValidResponse)
            _logger.LogError("Indexierung fehlgeschlagen für {Id}: {Error}",
                document.Id, response.DebugInformation);
    }

    private static string GetStableId(string absolutePath)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(absolutePath));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
    
    public async Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        var response = await _client.SearchAsync<FileDocument>(s => s
            .Index(IndexName)
            .From(request.From)
            .Size(request.Size)
            .Query(q => BuildQuery(q, request)),
            ct);

        if (!response.IsValidResponse)
        {
            _logger.LogError("Suche fehlgeschlagen: {Error}", response.DebugInformation);
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
                    .MultiMatch(mm => mm
                        .Query(request.Query)
                        .Fields(new[] { "content", "fileName" })
                        .Fuzziness(new Fuzziness("AUTO"))
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
                    .DateRange(d =>
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
            { "fileName",      new TextProperty { Analyzer = "standard" } },
            { "shareName",     new KeywordProperty() },
            { "absolutePath",  new KeywordProperty() },
            { "sharePath",     new KeywordProperty() },
            { "content",       new TextProperty { Analyzer = "standard" } },
            { "fileType",      new KeywordProperty() },
            { "fileSizeBytes", new LongNumberProperty() },
            { "created",       new DateProperty() },
            { "modified",      new DateProperty() },
            { "author",        new KeywordProperty() },
            { "tags",          new KeywordProperty() },
        };
    }
}
