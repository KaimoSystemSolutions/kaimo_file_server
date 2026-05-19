using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Storage;

namespace Kaimo_File_Server.Core.Services
{
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

        public async Task<List<FileMetadata>> ListAsync(string directoryPath, UserContext user)
        {
            // Prüfe ob der User das Verzeichnis überhaupt listen darf
            if (!await _acl.HasAccessAsync(user, _shareId, directoryPath, true, FilePermission.ListReadData))
                throw new UnauthorizedAccessException($"List denied for '{directoryPath}'");

            var items = await _storage.ListAsync(directoryPath);

            // Jedes Item einzeln prüfen — User sieht nur was er darf
            var visible = new List<FileMetadata>();
            foreach (var item in items)
            {
                // Relativen Pfad innerhalb des Shares berechnen
                var itemRelativePath = string.IsNullOrEmpty(directoryPath)
                    ? item.Name
                    : $"{directoryPath}/{item.Name}";

                if (await _acl.HasAccessAsync(user, _shareId, itemRelativePath, item.IsDirectory, FilePermission.ListReadData))
                    visible.Add(item);
            }

            return visible;
        }

        // ------------ Permission Checks ------------

        public async Task<bool> CanReadAsync(string path, UserContext user)
        {
            var isDir = await _storage.IsDirectoryAsync(path);
            return await _acl.HasAccessAsync(user, _shareId, path, isDir, FilePermission.ListReadData);
        }

        public async Task<bool> CanWriteAsync(string path, UserContext user)
        {
            var isDir = await _storage.IsDirectoryAsync(path);
            return await _acl.HasAccessAsync(user, _shareId, path, isDir, FilePermission.CreateWriteData);
        }

        public async Task<bool> CanCreateAsync(string path, UserContext user)
        {
            var parentPath = GetParentPath(path);
            return await _acl.HasAccessAsync(user, _shareId, parentPath, true, FilePermission.CreateWriteData);
        }

        public async Task<bool> CanDeleteAsync(string path, UserContext user)
        {
            var isDir = await _storage.IsDirectoryAsync(path);
            return await _acl.HasAccessAsync(user, _shareId, path, isDir, FilePermission.Delete);
        }

        public async Task<bool> CanListAsync(string path, UserContext user)
        {
            return await _acl.HasAccessAsync(user, _shareId, path, true, FilePermission.ListReadData);
        }

        // ------------ Full Operations ------------

        public async Task<Stream> ReadFileAsync(string path, UserContext user)
        {
            var isDir = await _storage.IsDirectoryAsync(path);
            if (!await _acl.HasAccessAsync(user, _shareId, path, isDir, FilePermission.ListReadData))
                throw new UnauthorizedAccessException($"Read denied for '{path}'");
            return await _storage.ReadAsync(path);
        }

        public async Task WriteFileAsync(string path, Stream data, UserContext user)
        {
            var isDir = await _storage.IsDirectoryAsync(path);
            if (!await _acl.HasAccessAsync(user, _shareId, path, isDir, FilePermission.CreateWriteData))
                throw new UnauthorizedAccessException($"Write denied for '{path}'");
            await _storage.WriteAsync(path, data);
        }

        public async Task CreateFileAsync(string path, UserContext user)
        {
            var parentPath = GetParentPath(path);
            if (!await _acl.HasAccessAsync(user, _shareId, parentPath, true, FilePermission.CreateWriteData))
                throw new UnauthorizedAccessException($"Create denied for '{path}'");
            await _storage.WriteAsync(path, Stream.Null);
        }

        public async Task DeleteFileAsync(string path, UserContext user)
        {
            var isDir = await _storage.IsDirectoryAsync(path);
            if (!await _acl.HasAccessAsync(user, _shareId, path, isDir, FilePermission.Delete))
                throw new UnauthorizedAccessException($"Delete denied for '{path}'");
            await _storage.DeleteAsync(path);
        }

        public async Task<FileMetadata> GetMetadataAsync(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);
            if (!await _acl.HasAccessAsync(user, _shareId, path, meta.IsDirectory, FilePermission.ListReadData))
                throw new UnauthorizedAccessException($"Metadata read denied for '{path}'");
            return meta;
        }

        // ------------ Helpers ------------

        private static string GetParentPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var normalized = path.TrimEnd('/');
            var lastSlash = normalized.LastIndexOf('/');
            return lastSlash > 0 ? normalized[..lastSlash] : "";
        }
    }
}