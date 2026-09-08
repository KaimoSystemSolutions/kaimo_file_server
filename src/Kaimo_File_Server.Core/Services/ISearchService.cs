using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Search;

public interface ISearchService
{
    Task onFileCreated(string absolutePath, Task<Stream> fileData, CancellationToken ct = default);
    Task onFileDeleted(string absolutePath);

    Task onDirectoryCreated(string absolutePath);
    Task onDirectoryDeleted(string absolutePath);

    /// <summary>
    /// Updates the index after a file has been renamed/moved. The stable id of a
    /// document is derived from its absolute path, so the document is re-indexed
    /// under the new id and the old entry is removed.
    /// </summary>
    Task onFileRenamed(string oldAbsolutePath, string newAbsolutePath);

    /// <summary>
    /// Updates the index after a directory has been renamed/moved. Every indexed
    /// document below the old path gets its paths/id rewritten to the new location.
    /// </summary>
    Task onDirectoryRenamed(string oldAbsolutePath, string newAbsolutePath);

    /// <summary>
    /// Searches the index and returns ONLY the documents the given user is allowed
    /// to read (ListReadData). The ACL check is mandatory and performed before any
    /// result leaves the service — never expose raw hits to the UI.
    ///
    /// Optionally scoped: when <paramref name="shareName"/> is set, only hits in that
    /// share are considered; when <paramref name="pathPrefix"/> is also set (a
    /// share-relative folder path), only that folder and everything below it. Both
    /// null (the default) keeps the original global-across-all-shares behavior.
    /// </summary>
    Task<List<FileDocument>> SearchAsync(
        string searchText, UserContext user,
        string? shareName = null, string? pathPrefix = null,
        CancellationToken ct = default);

    Task InitializeAsync(CancellationToken ct = default);
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
