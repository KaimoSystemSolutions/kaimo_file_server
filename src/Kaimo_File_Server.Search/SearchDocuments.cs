using System.Security.Cryptography;
using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Search;

/// <summary>
/// Document conventions shared by the index-backed search engines (Elasticsearch and the local
/// PostgreSQL index), so both derive identical ids, share-relative paths and reindex scopes.
/// </summary>
internal static class SearchDocuments
{
    /// <summary>Stable document id: lowercase hex <c>md5(absolutePath)</c>.</summary>
    public static string GetStableId(string absolutePath)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(absolutePath));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Builds an index document describing a directory: name only, no content.
    /// Returns <c>null</c> for share-root directories. Only folders *inside* a
    /// share are searchable entities.
    /// </summary>
    public static FileDocument? BuildDirectoryDocument(string absolutePath, ShareDefinition share)
    {
        string sharePath = Path.GetRelativePath(share.Path, absolutePath);
        if (sharePath is "." or "")
            return null;

        return new FileDocument
        {
            Id = GetStableId(absolutePath),
            FileName = Path.GetFileName(absolutePath.TrimEnd(Path.DirectorySeparatorChar)),
            ShareName = share.Name,
            AbsolutePath = absolutePath,
            SharePath = sharePath,
            Content = string.Empty,
            FileType = string.Empty,
            FileSizeBytes = 0,
            IsDirectory = true,
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        };
    }

    /// <summary>The most specific share whose path contains <paramref name="absolutePath"/>, or null.</summary>
    public static async Task<ShareDefinition?> ResolveShareAsync(
        IServiceScopeFactory scopeFactory, string absolutePath)
    {
        using var scope = scopeFactory.CreateScope();
        var shares = await scope.ServiceProvider
            .GetRequiredService<IShareRepository>()
            .GetAllAsync();

        var fullPath = Path.GetFullPath(absolutePath);
        return shares
            .Where(share => IsPathWithinShare(fullPath, share.Path))
            .OrderByDescending(share => Path.GetFullPath(share.Path).Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// Everything a full reindex walks: the enabled shares that exist on disk (optionally just
    /// <paramref name="shareName"/>) with their files and directories. Hidden/system folders
    /// (".versions", ".dp-keys", …) are skipped; the recycle bin is included
    /// (see <see cref="ShareEntryPolicy.IsExcludedFromSearch"/>).
    /// </summary>
    public static async Task<ReindexTargets> CollectReindexTargetsAsync(
        IServiceScopeFactory scopeFactory, string? shareName)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System
        };

        using var scope = scopeFactory.CreateScope();
        var shares = await scope.ServiceProvider
            .GetRequiredService<IShareRepository>()
            .GetAllEnabledAsync();

        var existingShares = shares
            .Where(s => Directory.Exists(s.Path))
            // Optional single-share scope: null/empty means reindex every share.
            .Where(s => string.IsNullOrEmpty(shareName)
                        || string.Equals(s.Name, shareName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var files = existingShares
            .SelectMany(share => Directory.EnumerateFiles(share.Path, "*", options)
                .Where(path => !IsExcluded(share, path))
                .Select(path => (Share: share, Path: path)))
            .ToList();

        // Directories are indexed too (by name), so folders show up in search.
        // Share-root directories are excluded via BuildDirectoryDocument.
        var dirs = existingShares
            .SelectMany(share => Directory.EnumerateDirectories(share.Path, "*", options)
                .Where(path => !IsExcluded(share, path))
                .Select(path => (Share: share, Path: path)))
            .ToList();

        return new ReindexTargets(existingShares, files, dirs);
    }

    private static bool IsExcluded(ShareDefinition share, string absolutePath)
        => ShareEntryPolicy.IsExcludedFromSearch(
            Path.GetRelativePath(share.Path, absolutePath), share.RecycleRootDepth);

    private static bool IsPathWithinShare(string fullPath, string sharePath)
    {
        var fullSharePath = Path.GetFullPath(sharePath).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                fullSharePath,
                PathComparison))
            return true;

        return fullPath.StartsWith(
            fullSharePath + Path.DirectorySeparatorChar,
            PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}

/// <summary>The shares, files and directories a reindex pass covers.</summary>
internal sealed record ReindexTargets(
    IReadOnlyList<ShareDefinition> Shares,
    IReadOnlyList<(ShareDefinition Share, string Path)> Files,
    IReadOnlyList<(ShareDefinition Share, string Path)> Directories);
