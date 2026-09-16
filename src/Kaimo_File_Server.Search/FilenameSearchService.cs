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
    // Over-fetch before ACL filtering so dropping unauthorized hits still leaves
    // enough to display; also bounds the filesystem walk on huge trees.
    private const int RawFetchSize = 200;

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

        var raw = await CollectRawMatchesAsync(searchText, shareName, pathPrefix, ct);
        if (raw.Count == 0)
            return raw;

        // SECURITY: every hit must pass an ACL check before it is exposed.
        return await _aclFilter.FilterAsync(raw, user, ct);
    }

    /// <summary>
    /// Enumerates files under each persisted share path and keeps those whose name contains
    /// the query. Hidden/system folders (".versions", ".dp-keys", recycle bin, …)
    /// are skipped — they are never part of the Elasticsearch index either.
    ///
    /// When <paramref name="shareName"/> is set the walk is restricted to that share,
    /// and when <paramref name="pathPrefix"/> is also set (a share-relative folder) the
    /// walk starts at that folder so only it and its descendants are considered.
    /// </summary>
    private async Task<List<FileDocument>> CollectRawMatchesAsync(
        string searchText, string? shareName, string? pathPrefix, CancellationToken ct)
    {
        var results = new List<FileDocument>();

        using var scope = _scopeFactory.CreateScope();
        var shares = await scope.ServiceProvider
            .GetRequiredService<IShareRepository>()
            .GetAllEnabledAsync();

        if (!string.IsNullOrWhiteSpace(shareName))
            shares = shares
                .Where(s => string.Equals(s.Name, shareName, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var relativePrefix = (pathPrefix ?? string.Empty).Replace('\\', '/').Trim('/');

        foreach (var share in shares)
        {
            ct.ThrowIfCancellationRequested();

            // Start the walk at the scoped subfolder when one is given, so "this
            // folder and below" is honored; otherwise at the share root.
            var walkRoot = relativePrefix.Length == 0
                ? share.Path
                : Path.Combine(share.Path, relativePrefix.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(walkRoot))
                continue;

            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    // Skip reparse points (symlinks/junctions): following one can loop
                    // forever on a user-created cycle or match a target outside the share.
                    // Cap recursion at the shared SafeDirectoryWalk depth.
                    AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
                    MaxRecursionDepth = SafeDirectoryWalk.DefaultMaxDepth
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

                    results.Add(CreateDocument(
                        share, absolutePath, relativePath, fileName, searchText,
                        isDirectory: false, size));

                    if (results.Count >= RawFetchSize)
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

                    results.Add(CreateDocument(
                        share, absolutePath, relativePath, folderName, searchText,
                        isDirectory: true, size: 0));

                    if (results.Count >= RawFetchSize)
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
