using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Services.File;

public partial class FileService
{
    // ------------------ ACL helpers ------------------

    /// <summary>
    /// Normalizes <paramref name="path"/>, resolves whether it is a directory from
    /// storage, and returns whether <paramref name="user"/> holds <paramref name="permission"/>.
    /// Shared by the boolean Can*Async probes.
    /// </summary>
    private async Task<bool> CheckAccessAsync(string path, UserContext user, FilePermission permission)
    {
        var normalized = ShareRelativePath.Normalize(path);
        if (IsInternalBelowRecycleRoot(normalized) ||
            IsUserWritePermission(permission) &&
            ShareEntryPolicy.IsReservedForUserWrites(normalized, _recycleRootDepth))
            return false;
        var isDir = await _storage.IsDirectoryAsync(normalized);
        return await _acl.HasAccessAsync(user, _shareId, normalized, isDir, permission);
    }

    /// <summary>
    /// Throws <see cref="UnauthorizedAccessException"/> unless <paramref name="user"/> holds
    /// <paramref name="permission"/> on the already-normalized <paramref name="normalizedPath"/>.
    /// Centralises the "check-or-throw" that every mutating/reading operation performs.
    /// </summary>
    private async Task EnsureAccessAsync(
        UserContext user, string normalizedPath, bool isDirectory, FilePermission permission)
    {
        if (IsUserWritePermission(permission))
            EnsureUserWriteTargetAllowed(normalizedPath);

        if (IsInternalBelowRecycleRoot(normalizedPath) ||
            !await _acl.HasAccessAsync(user, _shareId, normalizedPath, isDirectory, permission))
            throw new UnauthorizedAccessException(
                $"Access denied ({permission}) for '{normalizedPath}'");
    }

    // Transports reject share-root .kaimo-* paths during strict normalization, which does not
    // know the share; the per-home namespace (<userId>/.kaimo-*) is enforced here for every access.
    private bool IsInternalBelowRecycleRoot(string normalizedPath) =>
        _recycleRootDepth > 0 &&
        ShareEntryPolicy.IsInternalPath(normalizedPath, _recycleRootDepth);

    private static bool IsUserWritePermission(FilePermission permission) =>
        (permission &
            (FilePermission.CreateWriteData |
             FilePermission.CreateAppendData)) != 0;

    private void EnsureUserWriteTargetAllowed(string normalizedPath)
    {
        if (ShareEntryPolicy.IsReservedForUserWrites(normalizedPath, _recycleRootDepth))
            throw new UnauthorizedAccessException(
                $"Share path '{normalizedPath}' uses a reserved Kaimo namespace.");
    }

    // ------------------ Batch Permission Check ------------------

    public async Task<HashSet<string>> FilterReadablePathsAsync(
        IReadOnlyList<(string relativePath, bool isDirectory)> items,
        UserContext user)
    {
        if (items.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var normalized = items
            .Select(i => (ShareRelativePath.Normalize(i.relativePath), i.isDirectory))
            .ToList();

        var accessMap = await _acl.HasAccessBatchAsync(
            user, _shareId, normalized, FilePermission.ListReadData);

        var readable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, allowed) in accessMap)
        {
            if (allowed && !IsInternalBelowRecycleRoot(path))
                readable.Add(path);
        }

        return readable;
    }

    // ------------------ Permission Checks ------------------

    public Task<bool> CanReadAsync(string path, UserContext user)
        => CheckAccessAsync(path, user, FilePermission.ListReadData);

    public Task<bool> CanWriteAsync(string path, UserContext user)
        => CheckAccessAsync(path, user, FilePermission.CreateWriteData);

    public async Task<bool> CanCreateAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        if (ShareEntryPolicy.IsReservedForUserWrites(normalized, _recycleRootDepth))
            return false;
        var parentPath = ShareRelativePath.GetParent(path);
        return await _acl.HasAccessAsync(user, _shareId, parentPath, true, FilePermission.CreateWriteData);
    }

    public Task<bool> CanDeleteAsync(string path, UserContext user)
        => CheckAccessAsync(path, user, FilePermission.Delete);

    public async Task<bool> CanListAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        if (!ShareEntryPolicy.IsVisibleInFileBrowser(normalized, _recycleRootDepth))
            return false;
        return await _acl.HasAccessAsync(user, _shareId, normalized, true, FilePermission.ListReadData);
    }
}
