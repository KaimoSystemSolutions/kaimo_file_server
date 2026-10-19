using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Search;

public partial class ElasticSearchService
{
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
}
