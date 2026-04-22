using Kaimo_File_Server_Core.Core.Domain;
using Kaimo_File_Server_Core.Core.Security;
using Kaimo_File_Server_Core.Core.Storage;
using System;
using System.Collections.Generic;
using System.Text;
using static Kaimo_File_Server_Core.Core.Security.FilePermission;

namespace Kaimo_File_Server_Core.Core.Services
{
    /// <summary>
    /// Business logic layer, which handles all abstract file operations and permission checks for the file server. 
    /// This is the main entry point for all file-related operations, both for SMB and HTTP/Web.
    /// </summary>
    public class FileService
    {
        private readonly IStorageEngine _storage;
        private readonly IAclService _acl;

        public FileService(IStorageEngine storage, IAclService acl)
        {
            _storage = storage;
            _acl = acl;
        }

        // -------- PERMISSION CHECKS (für SMB) --------
        public bool CanReadSync(string path, UserContext user)
        {
            var meta = _storage.GetMetadataAsync(path).GetAwaiter().GetResult();
            return _acl.HasAccess(user, meta, FilePermission.ListReadData);
        }

        public bool CanWriteSync(string path, UserContext user)
        {
            var meta = _storage.GetMetadataAsync(path).GetAwaiter().GetResult();
            return _acl.HasAccess(user, meta, FilePermission.CreateWriteData);
        }

        public async Task<bool> CanRead(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);
            return _acl.HasAccess(user, meta, FilePermission.ListReadData);
        }

        public async Task<bool> CanWrite(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);
            return _acl.HasAccess(user, meta, FilePermission.CreateWriteData);
        }

        public async Task<bool> CanCreate(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);
            return _acl.HasAccess(user, meta, FilePermission.CreateWriteData);
        }

        public async Task<bool> CanDelete(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);
            return _acl.HasAccess(user, meta, FilePermission.Delete);
        }

        public async Task<bool> CanList(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);
            return _acl.HasAccess(user, meta, FilePermission.ListReadData);
        }

        // -------- FULL OPERATIONS (für HTTP/Web) --------
        public async Task<Stream> ReadFile(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);

            if (!_acl.HasAccess(user, meta, FilePermission.ListReadData))
                throw new UnauthorizedAccessException();

            return await _storage.ReadAsync(path);
        }

        public async Task WriteFile(string path, Stream data, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);

            if (!_acl.HasAccess(user, meta, FilePermission.CreateWriteData))
                throw new UnauthorizedAccessException();

            await _storage.WriteAsync(path, data);
        }

        public async Task CreateFile(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);

            if (!_acl.HasAccess(user, meta, FilePermission.CreateWriteData))
                throw new UnauthorizedAccessException();

            await _storage.WriteAsync(path, Stream.Null);
        }

        public async Task DeleteFile(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);

            if (!_acl.HasAccess(user, meta, FilePermission.Delete))
                throw new UnauthorizedAccessException();

            await _storage.DeleteAsync(path);
        }

        public async Task<FileMetadata> GetMetadata(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);

            if (!_acl.HasAccess(user, meta, FilePermission.ListReadData))
                throw new UnauthorizedAccessException();

            return meta;
        }

    }
}
