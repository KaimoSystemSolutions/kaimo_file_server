using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Search;

/// <summary>
/// Fallback search used when Elasticsearch is disabled or unreachable. It walks
/// the storage root and matches the query as a case-insensitive substring of the
/// file name only (no content indexing). Results pass the exact same
/// <see cref="SearchAclFilter"/> as the Elasticsearch backend, so visibility is
/// identical — only ranking/content-matching is weaker.
/// </summary>
public sealed class FilenameSearchService
{
    // Over-fetch before ACL filtering so dropping unauthorized hits still leaves
    // enough to display; also bounds the filesystem walk on huge trees.
    private const int RawFetchSize = 200;

    private readonly IStorageEngine _storage;
    private readonly SearchAclFilter _aclFilter;
    private readonly ILogger<FilenameSearchService> _logger;

    public FilenameSearchService(
        IStorageEngine storage,
        SearchAclFilter aclFilter,
        ILogger<FilenameSearchService> logger)
    {
        _storage = storage;
        _aclFilter = aclFilter;
        _logger = logger;
    }

    public async Task<List<FileDocument>> SearchAsync(
        string searchText, UserContext user, CancellationToken ct = default)
    {
        if (user is null || string.IsNullOrWhiteSpace(searchText))
            return new List<FileDocument>();

        var raw = CollectRawMatches(searchText, ct);
        if (raw.Count == 0)
            return raw;

        // SECURITY: every hit must pass an ACL check before it is exposed.
        return await _aclFilter.FilterAsync(raw, user, ct);
    }

    /// <summary>
    /// Enumerates files under the storage root and keeps those whose name contains
    /// the query. Hidden/system folders (".versions", ".dp-keys", recycle bin, …)
    /// are skipped — they are never part of the Elasticsearch index either.
    /// </summary>
    private List<FileDocument> CollectRawMatches(string searchText, CancellationToken ct)
    {
        var rootPath = _storage.getRootPath();
        var results = new List<FileDocument>();

        if (!Directory.Exists(rootPath))
            return results;

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System
            };

            // Match files by name...
            foreach (var absolutePath in Directory.EnumerateFiles(rootPath, "*", options))
            {
                ct.ThrowIfCancellationRequested();

                var fileName = Path.GetFileName(absolutePath);
                if (fileName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var relativePath = Path.GetRelativePath(rootPath, absolutePath);
                if (IsHiddenPath(relativePath))
                    continue;

                var segments = relativePath.Split(Path.DirectorySeparatorChar);
                var shareName = segments[0];
                var sharePath = Path.GetRelativePath(
                    Path.Combine(rootPath, shareName), absolutePath);

                long size = 0;
                try { size = new FileInfo(absolutePath).Length; }
                catch { /* file vanished mid-walk — keep going */ }

                results.Add(new FileDocument
                {
                    Id = absolutePath,
                    FileName = fileName,
                    ShareName = shareName,
                    AbsolutePath = absolutePath,
                    SharePath = sharePath,
                    Content = string.Empty,
                    FileType = Path.GetExtension(fileName).TrimStart('.'),
                    FileSizeBytes = size,
                    HighlightSnippet = Highlight(fileName, searchText)
                });

                if (results.Count >= RawFetchSize)
                    return results;
            }

            // ...and directories by name, so folders show up in search too.
            foreach (var absolutePath in Directory.EnumerateDirectories(rootPath, "*", options))
            {
                ct.ThrowIfCancellationRequested();

                var folderName = Path.GetFileName(absolutePath);
                if (folderName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var relativePath = Path.GetRelativePath(rootPath, absolutePath);
                if (IsHiddenPath(relativePath))
                    continue;

                var segments = relativePath.Split(
                    Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                // Skip share-root directories: only folders inside a share are hits.
                if (segments.Length < 2)
                    continue;

                var shareName = segments[0];
                var sharePath = Path.GetRelativePath(
                    Path.Combine(rootPath, shareName), absolutePath);

                results.Add(new FileDocument
                {
                    Id = absolutePath,
                    FileName = folderName,
                    ShareName = shareName,
                    AbsolutePath = absolutePath,
                    SharePath = sharePath,
                    Content = string.Empty,
                    FileType = string.Empty,
                    FileSizeBytes = 0,
                    IsDirectory = true,
                    HighlightSnippet = Highlight(folderName, searchText)
                });

                if (results.Count >= RawFetchSize)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Filename search walk failed under '{Root}'", rootPath);
        }

        return results;
    }

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
