using System.Text;
using System.Text.RegularExpressions;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Search;

/// <summary>
/// Full-text search backed by the application's PostgreSQL database — the "local indexing"
/// engine. Same contract and document shape as <see cref="ElasticSearchService"/>: content is
/// extracted by <see cref="ContentProvider"/>, hits pass <see cref="SearchAclFilter"/> in pages
/// (<see cref="SearchLimits"/>), and file/directory renames rewrite the stored documents.
/// Storage lives behind <see cref="ISearchIndexRepository"/> (Infrastructure), so this assembly
/// stays free of EF.
///
/// Differences to Elasticsearch: substring matching via trigram indexes instead of n-grams (content
/// from three characters on), no typo tolerance, ranking by weighted match counts instead of BM25,
/// and stored content is capped at <see cref="MaxContentChars"/>.
/// </summary>
public sealed partial class LocalIndexSearchService : ISearchService
{
    // ponytail: fixed cap on stored text per document to keep the trigram index bounded;
    // Elasticsearch stores all of it. Make it a setting if deeper content search is needed.
    internal const int MaxContentChars = 1_000_000;

    // Page size for pulling ranked hits before ACL filtering (same as Elasticsearch).
    private const int RawFetchSize = 50;

    // Upper bound on OR-combined query terms, so a pasted paragraph cannot build a huge statement.
    internal const int MaxQueryTokens = 10;

    private readonly ISearchIndexRepository _index;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SearchAclFilter _aclFilter;
    private readonly DemoModeOptions _demo;
    private readonly ILogger<LocalIndexSearchService> _logger;

    public LocalIndexSearchService(
        ISearchIndexRepository index,
        IServiceScopeFactory scopeFactory,
        SearchAclFilter aclFilter,
        DemoModeOptions demo,
        ILogger<LocalIndexSearchService> logger)
    {
        _index = index;
        _scopeFactory = scopeFactory;
        _aclFilter = aclFilter;
        _demo = demo;
        _logger = logger;
    }

    /// <summary>
    /// False in the read-only demo: the database rejects every write there, so indexing is
    /// skipped instead of failing on each change. The demo index is built beforehand.
    /// </summary>
    public bool CanWrite => !_demo.ReadOnly;

    // ══════════════════════════════════════════
    //  Indexing hooks
    // ══════════════════════════════════════════

    public async Task onFileCreated(string absolutePath, Task<Stream> fileData, CancellationToken ct = default)
    {
        if (!CanWrite)
        {
            await DisposeQuietlyAsync(fileData);
            return;
        }

        try
        {
            await IndexFileAsync(absolutePath, fileData, knownShare: null, Now(), ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted/renamed before the indexer got to it; the following log entry handles it.
            _logger.LogDebug("File vanished before indexing: {Path}", absolutePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local indexing failed for {Path}", absolutePath);
        }
    }

    public async Task onFileDeleted(string absolutePath)
    {
        if (!CanWrite) return;
        try
        {
            await _index.DeleteAsync(SearchDocuments.GetStableId(absolutePath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local index deletion failed for {Path}", absolutePath);
        }
    }

    public async Task onDirectoryCreated(string absolutePath)
    {
        if (!CanWrite) return;
        try
        {
            var share = await SearchDocuments.ResolveShareAsync(_scopeFactory, absolutePath);
            // Share-root directories (or paths outside every share) are not searchable folders.
            var doc = share is null ? null : SearchDocuments.BuildDirectoryDocument(absolutePath, share);
            if (doc is not null)
                await _index.UpsertAsync(doc, fileModifiedUtc: null, Now());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local directory indexing failed for {Path}", absolutePath);
        }
    }

    public async Task onDirectoryDeleted(string absolutePath)
    {
        if (!CanWrite) return;
        try
        {
            await _index.DeleteTreeAsync(absolutePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local index deletion failed for directory {Path}", absolutePath);
        }
    }

    public Task onFileRenamed(string oldAbsolutePath, string newAbsolutePath)
        => MoveAsync(oldAbsolutePath, newAbsolutePath);

    public Task onDirectoryRenamed(string oldAbsolutePath, string newAbsolutePath)
        => MoveAsync(oldAbsolutePath, newAbsolutePath);

    /// <summary>
    /// A file and a directory move are the same single statement: the moved entry and everything
    /// below it get their id, paths and share rewritten; the content is kept.
    /// </summary>
    private async Task MoveAsync(string oldAbsolutePath, string newAbsolutePath)
    {
        if (!CanWrite) return;
        try
        {
            var share = await SearchDocuments.ResolveShareAsync(_scopeFactory, newAbsolutePath);
            if (share is null)
            {
                _logger.LogWarning("Local index move skipped: no share contains '{Path}'", newAbsolutePath);
                return;
            }

            await _index.MoveAsync(
                oldAbsolutePath, newAbsolutePath,
                share.Name, Path.GetRelativePath(share.Path, newAbsolutePath), Now());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local index move failed for '{Old}'", oldAbsolutePath);
        }
    }

    /// <summary>
    /// Indexes one file. Unchanged files (same size and last-write time as stored) are only
    /// touched, so the content is not extracted again — this keeps a repeated reindex cheap.
    /// Throws on I/O errors; callers decide whether to log or skip.
    /// </summary>
    private async Task IndexFileAsync(
        string absolutePath, Task<Stream> fileData, ShareDefinition? knownShare,
        DateTime indexedUtc, CancellationToken ct)
    {
        var share = knownShare ?? await SearchDocuments.ResolveShareAsync(_scopeFactory, absolutePath);
        if (share is null)
        {
            await DisposeQuietlyAsync(fileData);
            _logger.LogWarning("Local indexing skipped because no share contains '{Path}'", absolutePath);
            return;
        }

        string id = SearchDocuments.GetStableId(absolutePath);
        string fileName = Path.GetFileName(absolutePath);
        string content;
        long size;
        DateTime? stamp;

        // IMPORTANT: dispose the read stream right after extraction. An open handle on a
        // Windows-backed bind mount blocks renaming/moving the containing directory.
        await using (var stream = await fileData)
        {
            var info = new FileInfo(absolutePath);
            size = info.Length; // throws FileNotFoundException when the file is gone
            // An SMB file still locked by its writer arrives as Stream.Null (name-only). Store no
            // stamp then, so the Modified entry on close re-extracts the real content.
            stamp = ReferenceEquals(stream, Stream.Null) ? null : ToDbPrecision(info.LastWriteTimeUtc);

            if (stamp is { } current && await _index.TouchIfUnchangedAsync(id, size, current, indexedUtc, ct))
                return;

            content = PrepareContent(await ContentProvider.GetContent(stream, fileName));
        }

        await _index.UpsertAsync(new FileDocument
        {
            Id = id,
            FileName = fileName,
            ShareName = share.Name,
            AbsolutePath = absolutePath,
            SharePath = Path.GetRelativePath(share.Path, absolutePath),
            Content = content,
            FileType = Path.GetExtension(fileName).TrimStart('.'),
            FileSizeBytes = size,
            Created = indexedUtc,
            Modified = indexedUtc
        }, stamp, indexedUtc, ct);
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

        var tokens = Tokenize(searchText);
        if (tokens.Count == 0)
            return new List<FileDocument>();

        // Page through the ranked hits until enough readable ones are collected, so matches
        // the user may not read cannot crowd out the ones they may.
        var allowed = new List<FileDocument>();
        for (int from = 0; from < SearchLimits.MaxRawScan; from += RawFetchSize)
        {
            var raw = await _index.SearchAsync(tokens, shareName, pathPrefix, from, RawFetchSize, includeRecycleBin, ct);
            foreach (var doc in raw)
                doc.HighlightSnippet = BuildHighlight(doc.FileName, doc.HighlightSnippet, tokens);

            // SECURITY: every hit must pass an ACL check before it is exposed.
            if (raw.Count > 0)
                allowed.AddRange(await _aclFilter.FilterAsync(raw, user, ct));

            if (raw.Count < RawFetchSize || allowed.Count >= SearchLimits.MinVisibleHits)
                break;
        }

        return allowed;
    }

    /// <summary>The schema is owned by EF migrations; nothing to prepare at startup.</summary>
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Splits the query like the Elasticsearch standard tokenizer + n-gram index do: runs of
    /// letters/digits, lowercased, at least two characters, de-duplicated. Anything else
    /// (punctuation, LIKE/regex metacharacters) never reaches the database.
    /// </summary>
    internal static IReadOnlyList<string> Tokenize(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return Array.Empty<string>();

        return TokenRegex().Matches(searchText)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(t => t.Length >= 2)
            .Distinct()
            .Take(MaxQueryTokens)
            .ToList();
    }

    /// <summary>
    /// Mirrors the Elasticsearch highlight preference: the file name when a term matches it,
    /// otherwise the content excerpt. Every occurrence of every term is wrapped in &lt;mark&gt;.
    /// </summary>
    internal static string? BuildHighlight(string fileName, string? excerpt, IReadOnlyList<string> tokens)
    {
        if (tokens.Any(t => fileName.Contains(t, StringComparison.OrdinalIgnoreCase)))
            return Mark(fileName, tokens);
        return string.IsNullOrEmpty(excerpt) ? null : Mark(excerpt, tokens);
    }

    internal static string Mark(string text, IReadOnlyList<string> tokens)
    {
        var marked = new bool[text.Length];
        foreach (var token in tokens)
            for (int i = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
                 i >= 0;
                 i = text.IndexOf(token, i + token.Length, StringComparison.OrdinalIgnoreCase))
                Array.Fill(marked, true, i, token.Length);

        var sb = new StringBuilder(text.Length + 16);
        for (int i = 0; i < text.Length; i++)
        {
            if (marked[i] && (i == 0 || !marked[i - 1])) sb.Append("<mark>");
            sb.Append(text[i]);
            if (marked[i] && (i == text.Length - 1 || !marked[i + 1])) sb.Append("</mark>");
        }
        return sb.ToString();
    }

    /// <summary>PostgreSQL text cannot hold NUL characters (common in PDF text); cap the size.</summary>
    internal static string PrepareContent(string content)
    {
        content = content.Replace("\0", string.Empty);
        return content.Length > MaxContentChars ? content[..MaxContentChars] : content;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex TokenRegex();

    // ══════════════════════════════════════════
    //  Reindex
    // ══════════════════════════════════════════

    /// <summary>
    /// Re-indexes every file and directory on disk (optionally one share). Unchanged files are
    /// only touched. After a complete pass, documents of the walked shares that were not touched
    /// — files deleted or moved out of band — are swept, so the index matches the disk again.
    /// A canceled pass sweeps nothing.
    /// </summary>
    public async Task ReindexAllAsync(
        IProgress<(int done, int total)>? progress, CancellationToken ct = default,
        string? shareName = null)
    {
        if (!CanWrite) return;

        var started = Now();
        var targets = await SearchDocuments.CollectReindexTargetsAsync(_scopeFactory, shareName);

        int total = targets.Files.Count + targets.Directories.Count;
        int done = 0;
        progress?.Report((0, total));

        foreach (var file in targets.Files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Task<Stream> data = Task.FromResult<Stream>(new FileStream(
                    file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true));
                await IndexFileAsync(file.Path, data, file.Share, started, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reindex: local indexing failed for '{Path}'", file.Path);
                // Keep the previous entry (if any) through the sweep: the file still exists,
                // it just could not be read right now (e.g. locked).
                await TryTouchAsync(file.Path, started, ct);
            }

            progress?.Report((++done, total));
        }

        foreach (var directory in targets.Directories)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var doc = SearchDocuments.BuildDirectoryDocument(directory.Path, directory.Share);
                if (doc is not null)
                    await _index.UpsertAsync(doc, fileModifiedUtc: null, started, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reindex: local indexing failed for directory '{Path}'", directory.Path);
                await TryTouchAsync(directory.Path, started, ct);
            }

            progress?.Report((++done, total));
        }

        ct.ThrowIfCancellationRequested();
        int swept = await _index.SweepAsync(targets.Shares.Select(s => s.Name).ToList(), started, ct);
        _logger.LogDebug(
            "Local reindex completed: {Done}/{Total} entries, {Swept} stale document(s) removed",
            done, total, swept);
    }

    public Task<SearchIndexStats> GetStatsAsync(CancellationToken ct = default) => _index.GetStatsAsync(ct);

    /// <summary>Makes the next reindex re-extract every file instead of skipping unchanged ones.</summary>
    public Task ResetContentStampsAsync(CancellationToken ct = default)
        => CanWrite ? _index.ResetContentStampsAsync(ct) : Task.CompletedTask;

    /// <summary>
    /// Returns the space of rewritten rows to the disk. Best effort: a failure only leaves the
    /// space reusable inside the table instead of freed.
    /// </summary>
    public async Task CompactAsync(CancellationToken ct = default)
    {
        if (!CanWrite) return;
        try
        {
            await _index.CompactAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Compacting the local search index failed");
        }
    }

    private async Task TryTouchAsync(string absolutePath, DateTime indexedUtc, CancellationToken ct)
    {
        try
        {
            await _index.TouchAsync(SearchDocuments.GetStableId(absolutePath), indexedUtc, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Reindex: could not keep the entry for '{Path}'", absolutePath);
        }
    }

    private static async Task DisposeQuietlyAsync(Task<Stream> fileData)
    {
        try
        {
            await (await fileData).DisposeAsync();
        }
        catch
        {
            // Nothing to release / already faulted — indexing was skipped anyway.
        }
    }

    /// <summary>PostgreSQL stores timestamps in microseconds; .NET ticks are 100 ns.</summary>
    private static DateTime ToDbPrecision(DateTime utc)
        => new(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);

    private static DateTime Now() => ToDbPrecision(DateTime.UtcNow);
}
