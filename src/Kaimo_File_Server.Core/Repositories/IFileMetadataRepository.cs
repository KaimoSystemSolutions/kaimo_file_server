using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Repositories
{
    public interface IFileMetadataRepository
    {
        Task<FileMetadata?> GetByPathAsync(string path);
        Task<FileMetadata> GetOrCreateAsync(string path, bool isDirectory);
    }
}