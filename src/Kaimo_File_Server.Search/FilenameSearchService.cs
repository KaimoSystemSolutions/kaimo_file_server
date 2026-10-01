using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Search;

/// <summary>
/// Fallback search used when Elasticsearch is disabled or unreachable. It walks
/// every enabled share path and matches the query as a case-insensitive substring
/// of the file name only (no content indexing). Results pass the exact same
/// <see cref="SearchAclFilter"/> as the Elasticsearch backend, so visibility is
/// identical — only ranking/content-matching is weaker.
/// </summary>
public sealed class FilenameSearchService
{
    // Raw matches are ACL-filtered in batches of this size during the walk; the walk
    // stops once enough readable hits are collected (see SearchLimits).
    private const int RawFetchSize = 200;

    // Each search walks whole directory trees, so concurrent walks are capped
    // process-wide; further searches wait (bounded by the caller's token) instead
    // of letting a burst of requests saturate the disks.
    // ponytail: fixed cap of 2 walks; make configurable if large installs need more
    private static readonly SemaphoreSlim WalkSlots = new(2, 2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SearchAclFilter _aclFilter;
    private readonly ILogger<FilenameSearchService> _logger;

    public FilenameSearchService(
        IServiceScopeFactory scopeFactory,
        SearchAclFilter aclFilter,
        ILogger<FilenameSearchService> logger)
    {
        _scopeFactory = scopeFactory;
        _aclFilter = aclFilter;
        _logger = logger;
    }

    public async Task<List<FileDocument>> SearchAsync(
        string searchText, UserContext user,
        string? shareName = null, string? pathPrefix = null,
        CancellationToken ct = default)
    {
        if (user is null || string.IsNullOrWhiteSpace(searchText))
            return new List<FileDocument>();

        await WalkSlots.WaitAsync(ct);
        try
        {
            return await CollectVisibleMatchesAsync(searchText, user, shareName, pathPrefix, ct);
        }
        finally
        {
            WalkSlots.Release();
        }
    }

    /// <summary>
    /// Enumerates files under each persisted share path and keeps those whose name contains
    /// the query and that the user may read. Hidden/system folders (".versions", ".dp-keys",
    /// recycle bin, …) are skipped — they are never part of the Elasticsearch index either.
    ///
    /// When <paramref name="shareName"/> is set the walk is restricted to that share,
    /// and when <paramref name="pathPrefix"/> is also set (a share-relative folder) the
    /// walk starts at that folder so only it and its descendants are considered.
    /// </summary>
    private async Task<List<FileDocument>> CollectVisibleMatchesAsync(
        string searchText, UserContext user, string? shareName, string? pathPrefix,
        CancellationToken ct)
    {
        var results = new List<FileDocument>();
        var batch = new List<FileDocument>(RawFetchSize);
        int scanned = 0;

        // SECURITY: every hit must pass an ACL check before it is exposed. Batches are
        // filtered as the walk goes, so unreadable matches cannot crowd out readable ones.
        async Task FlushAsync()
        {
            if (batch.Count > 0)
                results.AddRange(await _aclFilter.FilterAsync(batch, user, ct));
            batch.Clear();
        }

        // Adds a raw match; true once the walk can stop (enough hits or scan cap reached).
        async Task<bool> AddAsync(FileDocument doc)
        {
            batch.Add(doc);
            scanned++;
            if (batch.Count < RawFetchSize)
                return false;

            await FlushAsync();
            return results.Count >= SearchLimits.MinVisibleHits || scanned >= SearchLimits.MaxRawScan;
        }

        using var scope = _scopeFactory.CreateScope();
        var shares = await scope.ServiceProvider
            .GetRequiredService<IShareRepository>()
            .GetAllEnabledAsync();

        if (!string.IsNullOrWhiteSpace(shareName))
            shares = shares
                .Where(s => string.Equals(s.Name, shareName, StringComparison.OrdinalIgnoreCase))
                .ToList();

        // SECURITY: the prefix is untrusted (it reaches the client API). A traversal or
        // otherwise invalid prefix yields no results instead of walking outside a share.
        var relativePrefix = (pathPrefix ?? string.Empty).Replace('\\', '/').Trim('/');
        if (!ShareRelativePath.TryNormalizeStrict(relativePrefix, out relativePrefix))
            return results;

        foreach (var share in shares)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Start the walk at the scoped subfolder when one is given, so "this
                // folder and below" is honored; otherwise at the share root. Resolved
                // with a containment check as a second line of defense.
                var walkRoot = ShareRelativePath.ToContainedAbsolutePath(
                    share.Path, relativePrefix, allowInternalNamespace: false);
                if (!Directory.Exists(walkRoot))
                    continue;

                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System
                };

                // Match files by name...
                foreach (var absolutePath in Directory.EnumerateFiles(walkRoot, "*", options))
                {
                    ct.ThrowIfCancellationRequested();

                    var fileName = Path.GetFileName(absolutePath);
                    if (fileName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var relativePath = Path.GetRelativePath(share.Path, absolutePath);
                    if (IsHiddenPath(relativePath))
                        continue;

                    long size = 0;
                    try { size = new FileInfo(absolutePath).Length; }
                    catch { /* file vanished mid-walk — keep going */ }

                    if (await AddAsync(CreateDocument(
                            share, absolutePath, relativePath, fileName, searchText,
                            isDirectory: false, size)))
                        return results;
                }

                // ...and directories by name, so folders show up in search too.
                foreach (var absolutePath in Directory.EnumerateDirectories(walkRoot, "*", options))
                {
                    ct.ThrowIfCancellationRequested();

                    var folderName = Path.GetFileName(absolutePath);
                    if (folderName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var relativePath = Path.GetRelativePath(share.Path, absolutePath);
                    if (IsHiddenPath(relativePath))
                        continue;

                    if (await AddAsync(CreateDocument(
                            share, absolutePath, relativePath, folderName, searchText,
                            isDirectory: true, size: 0)))
                        return results;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Filename search walk failed under share '{Share}' at '{Path}'",
                    share.Name, share.Path);
            }
        }

        await FlushAsync();
        return results;
    }

    private static FileDocument CreateDocument(
        ShareDefinition share,
        string absolutePath,
        string relativePath,
        string name,
        string searchText,
        bool isDirectory,
        long size)
        => new()
        {
            Id = absolutePath,
            FileName = name,
            ShareName = share.Name,
            AbsolutePath = absolutePath,
            SharePath = relativePath,
            Content = string.Empty,
            FileType = isDirectory ? string.Empty : Path.GetExtension(name).TrimStart('.'),
            FileSizeBytes = size,
            IsDirectory = isDirectory,
            HighlightSnippet = Highlight(name, searchText)
        };

    /// <summary>True if any path segment is a hidden/system folder (starts with '.').</summary>
    private static bool IsHiddenPath(string relativePath)
    {
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar))
        {
            if (segment.StartsWith('.'))
                return true;
        }
        return false;
    }

    /// <summary>Wraps the first case-insensitive match of the query in &lt;mark&gt; tags.</summary>
    private static string Highlight(string fileName, string searchText)
    {
        var idx = fileName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return fileName;

        var before = fileName.Substring(0, idx);
        var match = fileName.Substring(idx, searchText.Length);
        var after = fileName.Substring(idx + searchText.Length);
        return $"{before}<mark>{match}</mark>{after}";
    }
}
