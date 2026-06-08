using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Storage;

namespace Kaimo_File_Server.Core.Services;

public class FileService : IFileService
{
    private readonly IStorageEngine _storage;
    private readonly IAclService _acl;
    private readonly IDepartmentPermissionService _deptPerm;
    private readonly Guid _shareId;

    private const string RecycleBinFolder = ".RECYCLE_BIN";

    /// <summary>
    /// Cache: department default permission per user ID for this share.
    /// Since the FileService instance is per-share, and department defaults
    /// don't vary by path (only by user + share), we can cache after first lookup.
    /// </summary>
    private readonly Dictionary<Guid, long> _deptPermCache = new();

    public FileService(
        IStorageEngine storage,
        IAclService acl,
        IDepartmentPermissionService deptPerm,
        Guid shareId)
    {
        _storage = storage;
        _acl = acl;
        _deptPerm = deptPerm;
        _shareId = shareId;
    }

    // ────────────────── Combined Permission Check ──────────────────

    /// <summary>
    /// Checks ACL first; if denied, falls back to department default permissions.
    /// Department defaults are additive (OR) — they grant baseline access for all
    /// members of the share's department.
    /// </summary>
    private async Task<bool> HasEffectiveAccessAsync(
        UserContext user, string normalizedPath, bool isDirectory, FilePermission required)
    {
        // 1. ACL grants access → done
        if (await _acl.HasAccessAsync(user, _shareId, normalizedPath, isDirectory, required))
            return true;

        // 2. Check department default permissions as additive fallback
        var deptPerm = await GetCachedDeptPermissionAsync(user);
        return DepartmentFilePermissionMapper.Grants(deptPerm, required);
    }

    /// <summary>
    /// Resolves the combined department default permission for a user on this share.
    /// Checks both direct user membership and group memberships.
    /// Cached per user ID for the lifetime of this FileService instance.
    /// </summary>
    private async Task<long> GetCachedDeptPermissionAsync(UserContext user)
    {
        if (_deptPermCache.TryGetValue(user.User.Id, out var cached))
            return cached;

        // User's own department membership
        long combined = await _deptPerm.GetEffectiveDefaultPermissionForUserOnShareAsync(
            user.User.Id, _shareId);

        // Group department memberships (union)
        foreach (var group in user.Groups)
        {
            var groupPerm = await _deptPerm.GetEffectiveDefaultPermissionForGroupOnShareAsync(
                group.Id, _shareId);
            combined |= groupPerm;
        }

        _deptPermCache[user.User.Id] = combined;
        return combined;
    }

    /// <summary>
    /// Batch version: checks ACL for all items first, then fills denied items
    /// with department default permission checks.
    /// Department permission is path-independent (same for all items on this share),
    /// so it's resolved once and applied to all ACL-denied items.
    /// </summary>
    private async Task<Dictionary<string, bool>> HasEffectiveAccessBatchAsync(
        UserContext user,
        IReadOnlyList<(string path, bool isDirectory)> items,
        FilePermission required)
    {
        // 1. Batch ACL check (single DB round trip)
        var aclResults = await _acl.HasAccessBatchAsync(user, _shareId, items, required);

        // 2. If ACL allows everything, skip department check
        if (aclResults.Values.All(v => v))
            return aclResults;

        // 3. Resolve department default once for this user + share
        var deptPerm = await GetCachedDeptPermissionAsync(user);
        var hasDeptAccess = DepartmentFilePermissionMapper.Grants(deptPerm, required);

        if (!hasDeptAccess)
            return aclResults; // No department access either → ACL result stands

        // 4. Merge: ACL-denied items get upgraded via department defaults
        var merged = new Dictionary<string, bool>(aclResults.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, aclAllowed) in aclResults)
        {
            merged[path] = aclAllowed || hasDeptAccess;
        }

        return merged;
    }

    // ────────────────── Directory Listing ──────────────────

    public async Task<List<FileMetadata>> ListAsync(string directoryPath, UserContext user)
    {
        var normalizedDir = ShareRelativePath.Normalize(directoryPath);

        if (!await HasEffectiveAccessAsync(user, normalizedDir, true, FilePermission.ListReadData))
            throw new UnauthorizedAccessException($"List denied for '{normalizedDir}'");

        var items = await _storage.ListAsync(normalizedDir);

        // Batch permission check for all children
        var itemsToCheck = items
            .Select(i => (ShareRelativePath.Combine(normalizedDir, i.Name), i.IsDirectory))
            .ToList();

        var accessMap = await HasEffectiveAccessBatchAsync(
            user, itemsToCheck, FilePermission.ListReadData);

        var visible = new List<FileMetadata>(items.Count);
        foreach (var item in items)
        {
            var itemPath = ShareRelativePath.Combine(normalizedDir, item.Name);
            if (accessMap.TryGetValue(itemPath, out var allowed) && allowed)
                visible.Add(item);
        }

        return visible;
    }

    // ────────────────── Batch Permission Check ──────────────────

    public async Task<HashSet<string>> FilterReadablePathsAsync(
        IReadOnlyList<(string relativePath, bool isDirectory)> items,
        UserContext user)
    {
        if (items.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var normalized = items
            .Select(i => (ShareRelativePath.Normalize(i.relativePath), i.isDirectory))
            .ToList();

        var accessMap = await HasEffectiveAccessBatchAsync(
            user, normalized, FilePermission.ListReadData);

        var readable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, allowed) in accessMap)
        {
            if (allowed)
                readable.Add(path);
        }

        return readable;
    }

    // ────────────────── Permission Checks ──────────────────

    public async Task<bool> CanReadAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);
        return await HasEffectiveAccessAsync(user, normalized, isDir, FilePermission.ListReadData);
    }

    public async Task<bool> CanWriteAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);
        return await HasEffectiveAccessAsync(user, normalized, isDir, FilePermission.CreateWriteData);
    }

    public async Task<bool> CanCreateAsync(string path, UserContext user)
    {
        var parentPath = ShareRelativePath.GetParent(path);
        return await HasEffectiveAccessAsync(user, parentPath, true, FilePermission.CreateWriteData);
    }

    public async Task<bool> CanDeleteAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);
        return await HasEffectiveAccessAsync(user, normalized, isDir, FilePermission.Delete);
    }

    public async Task<bool> CanListAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        return await HasEffectiveAccessAsync(user, normalized, true, FilePermission.ListReadData);
    }

    // ────────────────── Full Operations ──────────────────

    public async Task<Stream> ReadFileAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);

        if (!await HasEffectiveAccessAsync(user, normalized, isDir, FilePermission.ListReadData))
            throw new UnauthorizedAccessException($"Read denied for '{normalized}'");

        return await _storage.ReadAsync(normalized);
    }

    public async Task WriteFileAsync(string path, Stream data, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);

        if (!await HasEffectiveAccessAsync(user, normalized, isDir, FilePermission.CreateWriteData))
            throw new UnauthorizedAccessException($"Write denied for '{normalized}'");

        await _storage.WriteAsync(normalized, data);
    }

    public async Task CreateFileAsync(string path, UserContext user)
    {
        var parentPath = ShareRelativePath.GetParent(path);

        if (!await HasEffectiveAccessAsync(user, parentPath, true, FilePermission.CreateWriteData))
            throw new UnauthorizedAccessException($"Create denied for '{path}'");

        await _storage.WriteAsync(ShareRelativePath.Normalize(path), Stream.Null);
    }

    public async Task CreateDirectoryAsync(string path, UserContext user)
    {
        var parentPath = ShareRelativePath.GetParent(path);

        if (!await HasEffectiveAccessAsync(user, parentPath, true, FilePermission.CreateWriteData))
            throw new UnauthorizedAccessException($"Create denied for '{path}'");

        await _storage.CreateDirectory(ShareRelativePath.Normalize(path));
    }

    public async Task DeleteFileAsync(string path, UserContext user, bool isRecycleEnabled)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);

        if (!await HasEffectiveAccessAsync(user, normalized, isDir, FilePermission.Delete))
            throw new UnauthorizedAccessException($"Delete denied for '{normalized}'");

        var isAlreadyInRecycleBin = normalized.StartsWith(
            RecycleBinFolder, StringComparison.OrdinalIgnoreCase);

        if (isRecycleEnabled && !isAlreadyInRecycleBin)
        {
            var recyclePath = ShareRelativePath.Combine(RecycleBinFolder, normalized);
            await _storage.MoveAsync(normalized, recyclePath);
            await _acl.RenameAclPathAsync(_shareId, normalized, recyclePath);
        }
        else
        {
            await _storage.DeleteAsync(normalized);
            await _acl.DeleteAclAsync(_shareId, normalized);
        }
    }

    public async Task<FileMetadata> GetMetadataAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var meta = await _storage.GetMetadataAsync(normalized);

        if (!await HasEffectiveAccessAsync(user, normalized, meta.IsDirectory, FilePermission.ListReadData))
            throw new UnauthorizedAccessException($"Metadata read denied for '{normalized}'");

        return meta;
    }

    public async Task RenameAsync(string oldPath, string newPath, UserContext user)
    {
        var oldNormalized = ShareRelativePath.Normalize(oldPath);
        var newNormalized = ShareRelativePath.Normalize(newPath);
        var isDir = await _storage.IsDirectoryAsync(oldNormalized);

        if (!await HasEffectiveAccessAsync(user, oldNormalized, isDir, FilePermission.Delete))
            throw new UnauthorizedAccessException($"Rename (delete) denied for '{oldNormalized}'");
        if (!await HasEffectiveAccessAsync(user, newNormalized, isDir, FilePermission.CreateWriteData))
            throw new UnauthorizedAccessException($"Rename (create) denied for '{newNormalized}'");

        if (isDir)
        {
            await _storage.RenameDirectoryAsync(oldNormalized, newNormalized);
            await _acl.RenameAclPathAsync(_shareId, oldNormalized, newNormalized);
        }
        else
        {
            await _storage.RenameFileAsync(oldNormalized, newNormalized);
            await _acl.RenameAclPathAsync(_shareId, oldNormalized, newNormalized);
        }
    }

    public async Task<long> GetDirectorySizeAsync(string relativePath, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(relativePath);

        if (!await HasEffectiveAccessAsync(user, normalized, true, FilePermission.ListReadData))
            throw new UnauthorizedAccessException($"Size read denied for '{normalized}'");

        return await _storage.GetDirectorySizeAsync(normalized);
    }
}