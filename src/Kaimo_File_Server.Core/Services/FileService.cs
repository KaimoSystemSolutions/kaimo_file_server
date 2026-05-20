using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Storage;

namespace Kaimo_File_Server.Core.Services;

public class FileService : IFileService
{
    private readonly IStorageEngine _storage;
    private readonly IAclService _acl;
    private readonly Guid _shareId;

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

        // Filter each item individually — user only sees what they're allowed to
        var visible = new List<FileMetadata>();
        foreach (var item in items)
        {
            var itemPath = ShareRelativePath.Combine(normalizedDir, item.Name);

            if (await _acl.HasAccessAsync(user, _shareId, itemPath, item.IsDirectory, FilePermission.ListReadData))
                visible.Add(item);
        }

        return visible;
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

    public async Task DeleteFileAsync(string path, UserContext user)
    {
        var normalized = ShareRelativePath.Normalize(path);
        var isDir = await _storage.IsDirectoryAsync(normalized);

        if (!await _acl.HasAccessAsync(user, _shareId, normalized, isDir, FilePermission.Delete))
            throw new UnauthorizedAccessException($"Delete denied for '{normalized}'");

        await _storage.DeleteAsync(normalized);
        await _acl.DeleteAclAsync(_shareId, normalized);
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