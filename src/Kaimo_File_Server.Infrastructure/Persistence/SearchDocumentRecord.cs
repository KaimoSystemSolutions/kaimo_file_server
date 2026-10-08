namespace Kaimo_File_Server.Infrastructure.Persistence;

/// <summary>
/// One document of the local search index (<c>search_documents</c>). Mirrors the fields of the
/// Elasticsearch document; the index is derived data and fully rebuildable by a reindex.
/// Only written through raw SQL in <see cref="Repositories.SearchIndexRepository"/>; the entity
/// exists so the table, the trigram indexes and the extension are owned by migrations.
/// </summary>
public sealed class SearchDocumentRecord
{
    /// <summary><c>md5(AbsolutePath)</c> as lowercase hex — the same id Elasticsearch uses.</summary>
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ShareName { get; set; } = string.Empty;
    public string AbsolutePath { get; set; } = string.Empty;
    public string SharePath { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public string Content { get; set; } = string.Empty;

    /// <summary>Last-write time of the file when its content was extracted; null = re-extract on next write.</summary>
    public DateTime? FileModifiedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }

    /// <summary>Last time the document was written or confirmed; drives the reindex sweep.</summary>
    public DateTime IndexedUtc { get; set; }
}
