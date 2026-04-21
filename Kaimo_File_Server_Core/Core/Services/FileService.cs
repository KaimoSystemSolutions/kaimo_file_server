using Kaimo_File_Server_Core.Core.Domain;
using Kaimo_File_Server_Core.Core.Security;
using Kaimo_File_Server_Core.Core.Storage;
using System;
using System.Collections.Generic;
using System.Text;
using static Kaimo_File_Server_Core.Core.Security.FilePermissions;

namespace Kaimo_File_Server_Core.Core.Services
{
    public class FileService
    {
        private readonly IStorageEngine _storage;
        private readonly IAclService _acl;

        public FileService(IStorageEngine storage, IAclService acl)
        {
            _storage = storage;
            _acl = acl;
        }

        public async Task<Stream> ReadFile(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);

            if (!_acl.HasAccess(user, meta, FilePermission.Read))
                throw new UnauthorizedAccessException();

            return await _storage.ReadAsync(path);
        }

        public async Task WriteFile(string path, Stream data, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);

            if (!_acl.HasAccess(user, meta, FilePermission.Write))
                throw new UnauthorizedAccessException();

            await _storage.WriteAsync(path, data);
        }

        public async Task CreateFile(string path, UserContext user)
        {
            var meta = await _storage.GetMetadataAsync(path);

            if (!_acl.HasAccess(user, meta, FilePermission.Create))
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

    }
}
