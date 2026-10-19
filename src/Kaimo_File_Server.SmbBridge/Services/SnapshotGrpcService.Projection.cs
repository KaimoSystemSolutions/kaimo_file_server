using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;

namespace Kaimo_File_Server.SmbBridge.Services;

public partial class SnapshotGrpcService
{
    private static void ReconcileUserProjection(
        string cacheRoot,
        Guid shareId,
        string gmtToken,
        string cacheScope,
        string folderPath,
        IReadOnlyCollection<FileVersion> readableVersions,
        CancellationToken cancellationToken)
    {
        string scopeRoot = SnapshotCache.EnsureUserScope(
            cacheRoot, shareId, gmtToken, cacheScope);
        if (!ShareRelativePath.TryNormalizeStrict(
                folderPath, out string normalizedFolder,
                allowRoot: true, allowInternalNamespace: false))
            throw new InvalidDataException(
                "Folder projection contains an invalid path.");
        string folderFull = normalizedFolder.Length == 0
            ? scopeRoot
            : ShareRelativePath.ToContainedAbsolutePath(
                scopeRoot, normalizedFolder, allowRoot: false,
                allowInternalNamespace: false);
        SnapshotCache.EnsureDirectory(folderFull);

        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var allowed = readableVersions
            .Where(v => ShareRelativePath.TryNormalizeStrict(
                v.FilePath, out _, allowRoot: false,
                allowInternalNamespace: false))
            .Select(v => GetScopedCachePath(scopeRoot, v.FilePath))
            .ToHashSet(pathComparer);

        foreach (string file in Directory.EnumerateFiles(
                     folderFull, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!allowed.Contains(Path.GetFullPath(file)))
                File.Delete(file);
        }

        // Remove stale empty directories left behind by revoked files, but retain
        // the requested directory that Samba is about to traverse.
        foreach (string directory in Directory.EnumerateDirectories(
                     folderFull, "*", SearchOption.AllDirectories)
                 .OrderByDescending(path => path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }

    private string ProjectionPath(
        Guid shareId, string gmtToken, string cacheScope, FileVersion version)
    {
        string scopeRoot = SnapshotCache.EnsureUserScope(
            _cacheRoot, shareId, gmtToken, cacheScope);
        return GetScopedCachePath(scopeRoot, version.FilePath);
    }

    private void CleanupAbandonedProjection(IEnumerable<string> createdFiles)
    {
        foreach (string path in createdFiles.Reverse())
        {
            try
            {
                File.Delete(path);
                string? directory = Path.GetDirectoryName(path);
                while (directory is not null &&
                       !string.Equals(
                           Path.GetFullPath(directory),
                           Path.GetFullPath(_cacheRoot),
                           OperatingSystem.IsWindows()
                               ? StringComparison.OrdinalIgnoreCase
                               : StringComparison.Ordinal) &&
                       Directory.Exists(directory) &&
                       !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                    directory = Path.GetDirectoryName(directory);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to remove abandoned snapshot projection file {Path}",
                    path);
            }
        }
    }

    private static string GetScopedCachePath(
        string scopeRoot, string relativePath)
    {
        try
        {
            return ShareRelativePath.ToContainedAbsolutePath(
                scopeRoot, relativePath, allowRoot: false,
                allowInternalNamespace: false);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidDataException(
                "Version path escapes the user snapshot cache scope.",
                exception);
        }
    }

    private static string GetCachePath(string cacheRoot, string relativePath)
    {
        return GetScopedCachePath(cacheRoot, relativePath);
    }
}
