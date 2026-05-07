using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Storage;

namespace Kaimo_File_Server.Core.Services
{
    /// <summary>
    /// Business logic layer for all file operations and permission checks.
    /// 
    /// This is the single entry point for every transport (SMB, HTTP, NFS).
    /// All methods are async and require an explicit UserContext — no ambient state.
    /// </summary>
    public class FileService : IFileService
    {
        private readonly IStorageEngine _storage;
        private readonly IAclService _acl;

        public FileService(IStorageEngine storage, IAclService acl)
        {
            _storage = storage;
            _acl = acl;
        }

        // ──────────── Permission Checks ────────────

        public async Task<bool> CanReadAsync(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);
            return _acl.HasAccess(user, meta, FilePermission.ListReadData);
        }

        public async Task<bool> CanWriteAsync(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);
            return _acl.HasAccess(user, meta, FilePermission.CreateWriteData);
        }

        public async Task<bool> CanCreateAsync(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);
            return _acl.HasAccess(user, meta, FilePermission.CreateWriteData);
        }

        public async Task<bool> CanDeleteAsync(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);
            return _acl.HasAccess(user, meta, FilePermission.Delete);
        }

        public async Task<bool> CanListAsync(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);
            return _acl.HasAccess(user, meta, FilePermission.ListReadData);
        }

        // ──────────── Full Operations ────────────

        public async Task<Stream> ReadFileAsync(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);

            if (!_acl.HasAccess(user, meta, FilePermission.ListReadData))
                throw new UnauthorizedAccessException($"Read denied for '{path}'");

            return await _storage.ReadAsync(path);
        }

        public async Task WriteFileAsync(string path, Stream data, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);

            if (!_acl.HasAccess(user, meta, FilePermission.CreateWriteData))
                throw new UnauthorizedAccessException($"Write denied for '{path}'");

            await _storage.WriteAsync(path, data);
        }

        public async Task CreateFileAsync(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);

            if (!_acl.HasAccess(user, meta, FilePermission.CreateWriteData))
                throw new UnauthorizedAccessException($"Create denied for '{path}'");

            await _storage.WriteAsync(path, Stream.Null);
        }

        public async Task DeleteFileAsync(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);

            if (!_acl.HasAccess(user, meta, FilePermission.Delete))
                throw new UnauthorizedAccessException($"Delete denied for '{path}'");

            await _storage.DeleteAsync(path);
        }

        public async Task<FileMetadata> GetMetadataAsync(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);

            if (!_acl.HasAccess(user, meta, FilePermission.ListReadData))
                throw new UnauthorizedAccessException($"Metadata read denied for '{path}'");

            return meta;
        }
    }
}