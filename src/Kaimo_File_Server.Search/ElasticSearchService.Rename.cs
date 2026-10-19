using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Search;

public partial class ElasticSearchService
{
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
}
