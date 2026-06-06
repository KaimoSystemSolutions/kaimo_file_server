using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Web.Helpers;

namespace Kaimo_File_Server.Core.Services;

public class FileService : IFileService
{
    private readonly IStorageEngine _storage;
    private readonly IAclService _acl;
    private readonly Guid _shareId;

    private const string RecycleBinFolder = ".RECYCLE_BIN";

    public FileService(IStorageEngine storage, IAclService acl, Guid shareId)
    {
        _storage = storage;
        _acl = acl;
        _shareId = shareId;
    }

    // ────────────────── Directory Listing ──────────────────

    public async Task<List<FileMetadata>> ListAsync(string directoryPath, UserContext user)
    {
        var normalizedDir = ShareRelativePath.Normalize(directoryPath);

        if (!await _acl.HasAccessAsync(user, _shareId, normalizedDir, true, FilePermission.ListReadData))
            throw new UnauthorizedAccessException($"List denied for '{normalizedDir}'");

        var items = await _storage.ListAsync(normalizedDir);

        // Batch ACL check — single DB round trip for all children
        var itemsToCheck = items
            .Select(i => (ShareRelativePath.Combine(normalizedDir, i.Name), i.IsDirectory))
            .ToList();

        var accessMap = await _acl.HasAccessBatchAsync(
            user, _shareId, itemsToCheck, FilePermission.ListReadData);

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

    /// <summary>
    /// Returns the subset of paths the user can read — single DB round trip.
    /// Used by SMB QueryDirectory to filter listings efficiently.
    /// </summary>
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
        return await _acl.HasAccessAsync(user, _shareId, normalized, isDir, FilePermission.ListReadData);
    }

    public async Task<bool> CanWriteAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);
        return await _acl.HasAccessAsync(user, _shareId, normalized, isDir, FilePermission.CreateWriteData);
    }

    public async Task<bool> CanCreateAsync(string path, UserContext user)
    {
        var parentPath = ShareRelativePath.GetParent(path);
        return await _acl.HasAccessAsync(user, _shareId, parentPath, true, FilePermission.CreateWriteData);
    }

    public async Task<bool> CanDeleteAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);
        return await _acl.HasAccessAsync(user, _shareId, normalized, isDir, FilePermission.Delete);
    }

    public async Task<bool> CanListAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        return await _acl.HasAccessAsync(user, _shareId, normalized, true, FilePermission.ListReadData);
    }

    // ────────────────── Full Operations ──────────────────

    public async Task<Stream> ReadFileAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);

        if (!await _acl.HasAccessAsync(user, _shareId, normalized, isDir, FilePermission.ListReadData))
            throw new UnauthorizedAccessException($"Read denied for '{normalized}'");

        return await _storage.ReadAsync(normalized);
    }

    public async Task WriteFileAsync(string path, Stream data, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);

        if (!await _acl.HasAccessAsync(user, _shareId, normalized, isDir, FilePermission.CreateWriteData))
            throw new UnauthorizedAccessException($"Write denied for '{normalized}'");

        await _storage.WriteAsync(normalized, data);
    }
    

    public async Task CreateFileAsync(string path, UserContext user)
    {
        var parentPath = ShareRelativePath.GetParent(path);

        if (!await _acl.HasAccessAsync(user, _shareId, parentPath, true, FilePermission.CreateWriteData))
            throw new UnauthorizedAccessException($"Create denied for '{path}'");

        await _storage.WriteAsync(ShareRelativePath.Normalize(path), Stream.Null);
    }

    public async Task CreateDirectoryAsync(string path, UserContext user)
    {
        var parentPath = ShareRelativePath.GetParent(path);

        if (!await _acl.HasAccessAsync(user, _shareId, parentPath, true, FilePermission.CreateWriteData))
            throw new UnauthorizedAccessException($"Create denied for '{path}'");

        await _storage.CreateDirectory(ShareRelativePath.Normalize(path));
    }

    public async Task DeleteFileAsync(string path, UserContext user, bool isRecycleEnabled)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);

        if (!await _acl.HasAccessAsync(user, _shareId, normalized, isDir, FilePermission.Delete))
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

        if (!await _acl.HasAccessAsync(user, _shareId, normalized, meta.IsDirectory, FilePermission.ListReadData))
            throw new UnauthorizedAccessException($"Metadata read denied for '{normalized}'");

        return meta;
    }

    public async Task RenameAsync(string oldPath, string newPath, UserContext user)
    {
        var oldNormalized = ShareRelativePath.Normalize(oldPath);
        var newNormalized = ShareRelativePath.Normalize(newPath);
        var isDir = await _storage.IsDirectoryAsync(oldNormalized);

        if (!await _acl.HasAccessAsync(user, _shareId, oldNormalized, isDir, FilePermission.Delete))
            throw new UnauthorizedAccessException($"Rename (delete) denied for '{oldNormalized}'");
        if (!await _acl.HasAccessAsync(user, _shareId, newNormalized, isDir, FilePermission.CreateWriteData))
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

        if (!await _acl.HasAccessAsync(user, _shareId, normalized, true, FilePermission.ListReadData))
            throw new UnauthorizedAccessException($"Size read denied for '{normalized}'");

        return await _storage.GetDirectorySizeAsync(normalized);
    }
}