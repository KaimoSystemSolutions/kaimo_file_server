using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Search;

public class ElasticSearchService : ISearchService
{
    private const string IndexName = "kaimo-files";

    private readonly ElasticsearchClient _client;
    private readonly ILogger<ElasticSearchService> _logger;

    private enum ExistsResult
    {
        ExactFileExists,
        OldVersionExists,
        DoesntExist
    }
    
    public ElasticSearchService(ElasticsearchClient client, ILogger<ElasticSearchService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var exists = await _client.Indices.ExistsAsync(IndexName, ct);
        if (exists.Exists)
            return;

        var response = await _client.Indices.CreateAsync(IndexName, c => c
            .Mappings(m => m
                .Properties(new Properties
                {
                    { "id",            new KeywordProperty() },
                    { "fileName",      new TextProperty { Analyzer = "standard" } },
                    { "filePath",      new KeywordProperty() },
                    { "content",       new TextProperty { Analyzer = "standard" } },
                    { "fileType",      new KeywordProperty() },
                    { "fileSizeBytes", new LongNumberProperty() },
                    { "created",       new DateProperty() },
                    { "modified",      new DateProperty() },
                    { "author",        new KeywordProperty() },
                    { "tags",          new KeywordProperty() },
                })
            ), ct);

        if (!response.IsValidResponse)
            _logger.LogError("Index konnte nicht erstellt werden: {Error}", response.DebugInformation);
        else
            _logger.LogInformation("Index '{Index}' erstellt", IndexName);
    }


    private async Task<ExistsResult> CheckForDocumentsExistance(FileDocument document, CancellationToken ct = default)
    {
            
        var existing = await _client.GetAsync<FileDocument>(document.Id, g => g
            .Index(IndexName));

        if (!existing.Found)
            return ExistsResult.DoesntExist;

        var existingFile = existing.Source;

        bool sameContent = document.Content.Equals(existingFile?.Content);
        bool samePath = document.FilePath.Equals(existingFile?.FilePath) && document.FileName.Equals(existingFile.FileName);

        if (samePath && sameContent)
            return ExistsResult.ExactFileExists;
        
        if (samePath)
            return ExistsResult.OldVersionExists;

        return ExistsResult.DoesntExist;
    }
    
    public async Task IndexDocumentIfNotExistsAsync(string absolutePath, CancellationToken ct = default)
    {
        string fileName = Path.GetFileName(absolutePath);
    
        FileDocument document = new FileDocument
        {
            Id = Guid.NewGuid().ToString(),
            FileName = fileName,
            FilePath = absolutePath,
            Content = "ich mag schuhe",
            FileType = Path.GetExtension(fileName).TrimStart('.'),
            FileSizeBytes = new FileInfo(absolutePath).Length,
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        };
        
        ExistsResult existenceCheck = await CheckForDocumentsExistance(document, ct);

        switch (existenceCheck)
        {
            case ExistsResult.ExactFileExists:
                _logger.LogDebug("Dokument {Id} bereits indexiert, überspringe", document.Id);
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

    public async Task IndexManyAsync(IEnumerable<FileDocument> documents, CancellationToken ct = default)
    {
        var response = await _client.BulkAsync(b => b
            .Index(IndexName)
            .IndexMany(documents),
            ct);

        if (response.Errors)
            foreach (var item in response.ItemsWithErrors)
                _logger.LogError("Bulk-Fehler für {Id}: {Error}", item.Id, item.Error?.Reason);
    }

    public async Task DeleteDocumentAsync(string id, CancellationToken ct = default)
    {
        await _client.DeleteAsync<FileDocument>(IndexName, id, ct);
        
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
}
