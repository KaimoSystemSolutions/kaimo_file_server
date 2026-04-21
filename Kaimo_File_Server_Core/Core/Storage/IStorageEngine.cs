using Kaimo_File_Server_Core.Core.Domain;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server_Core.Core.Storage
{
    public interface IStorageEngine
    {
        Task<Stream> ReadAsync(string path);
        Task WriteAsync(string path, Stream data);
        Task DeleteAsync(string path);
        Task<FileMetadata> GetMetadataAsync(string path);
    }
}
