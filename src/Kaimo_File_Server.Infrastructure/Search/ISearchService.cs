namespace Kaimo_File_Server.Infrastructure.Search;

public interface ISearchService
{
    Task InitializeAsync(CancellationToken ct = default);
    Task IndexDocumentAsync(FileDocument document, CancellationToken ct = default);
    Task IndexManyAsync(IEnumerable<FileDocument> documents, CancellationToken ct = default);
    Task DeleteDocumentAsync(string id, CancellationToken ct = default);
    Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken ct = default);
}

public class SearchRequest
{
    public string Query { get; set; } = string.Empty;
    public string? FileType { get; set; }
    public DateTime? CreatedAfter { get; set; }
    public DateTime? CreatedBefore { get; set; }
    public int From { get; set; } = 0;
    public int Size { get; set; } = 20;
}

public class SearchResult
{
    public long TotalHits { get; set; }
    public List<SearchHit> Hits { get; set; } = [];
}

public class SearchHit
{
    public required FileDocument Document { get; set; }
    public double Score { get; set; }
}
