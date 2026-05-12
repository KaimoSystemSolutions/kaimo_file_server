using Kaimo_File_Server.Core.Domain;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Storage
{
    public interface IStorageEngine
    {
        Task<Stream> ReadAsync(string path);
        Task WriteAsync(string path, Stream data);
        Task DeleteAsync(string path);
        Task<FileMetadata> GetMetadataAsync(string path);
        Task<List<FileMetadata>> ListAsync(string directoryPath);
        Task CreateDirectoryAsync(string path);
    }
}
