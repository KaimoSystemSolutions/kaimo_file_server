using Kaimo_File_Server.Search;

namespace Kaimo_File_Server.Core.Repositories;

/// <summary>
/// Storage of the local (PostgreSQL) search index. Documents are keyed by the same stable id
/// as in Elasticsearch (<c>md5(absolutePath)</c>). Every method is a single atomic statement
/// (or one short transaction), so the index never shows a half-applied rename or delete.
/// </summary>
public interface ISearchIndexRepository
{
    /// <summary>
    /// Inserts or replaces a document. The original creation time of an existing document is
    /// kept. <paramref name="fileModifiedUtc"/> is the dedupe stamp; null forces the next write
    /// to re-extract the content.
    /// </summary>
    Task UpsertAsync(FileDocument doc, DateTime? fileModifiedUtc, DateTime indexedUtc, CancellationToken ct = default);

    /// <summary>
    /// Marks a document as current when its stored size and modification time still match the
    /// file on disk. Returns false when the document is missing or stale (content must be re-read).
    /// </summary>
    Task<bool> TouchIfUnchangedAsync(string id, long sizeBytes, DateTime fileModifiedUtc, DateTime indexedUtc, CancellationToken ct = default);

    /// <summary>Clears every dedupe stamp, so the next write of each file re-extracts its content.</summary>
    Task ResetContentStampsAsync(CancellationToken ct = default);

    /// <summary>Rewrites the table and its indexes compactly, returning freed space to the disk.</summary>
    Task CompactAsync(CancellationToken ct = default);

    /// <summary>Marks a document as current regardless of its content (keeps it through a sweep).</summary>
    Task TouchAsync(string id, DateTime indexedUtc, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>Deletes a directory document and every document below it.</summary>
    Task<int> DeleteTreeAsync(string absolutePath, CancellationToken ct = default);

    /// <summary>
    /// Moves a file or a whole directory subtree: rewrites id, paths, share and (for the moved
    /// entry itself) file name and type, and marks the moved documents as current
    /// (<paramref name="indexedUtc"/>). Documents already present at the target are replaced.
    /// </summary>
    Task<int> MoveAsync(
        string oldAbsolutePath, string newAbsolutePath,
        string newShareName, string newSharePath, DateTime indexedUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Raw, ranked, UNFILTERED search page. Each token matches as a case-insensitive substring of
    /// the file name and, from three characters on, of the content. <see cref="FileDocument.HighlightSnippet"/>
    /// carries an unmarked content excerpt around the first content match (or null).
    /// Recycle-bin documents are left out unless <paramref name="includeRecycleBin"/> is set.
    /// Results must pass the ACL filter before they are exposed.
    /// </summary>
    Task<List<FileDocument>> SearchAsync(
        IReadOnlyList<string> tokens, string? shareName, string? pathPrefix,
        int offset, int limit, bool includeRecycleBin = false, CancellationToken ct = default);

    /// <summary>Deletes documents of the given shares that were not touched since <paramref name="indexedBefore"/>.</summary>
    Task<int> SweepAsync(IReadOnlyCollection<string> shareNames, DateTime indexedBefore, CancellationToken ct = default);

    Task<SearchIndexStats> GetStatsAsync(CancellationToken ct = default);
}
